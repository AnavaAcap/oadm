using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Tools;
using Oadm.Sites.Transport;

namespace Oadm.Server.Mcp;

/// <summary>
/// Who calls the MCP server (docs/spec/mcp.md "Access keys"): the access key of <c>Authorization: Bearer</c>, an identity of
/// its own with its role and devices as stored at this call (a role change or new group rules apply to the next call; a
/// deleted or ended key ends at once), at most <see cref="CallsPerSecond"/> calls per second, <see cref="CallsAtOnce"/> calls
/// waiting for their answer (every device tool reads the whole view) and <see cref="ChangesPerMinute"/> changes a minute per
/// key, at most <see cref="CallsInPort"/> calls waiting at once on the whole port, file transfers in a pool of their own
/// (<see cref="TransfersAtOnce"/> per key, <see cref="TransfersInPort"/> on the port), a refused call counting against
/// nothing, and every refused key in the audit log (one entry per address, IPv6 per /64, and key a minute, its count in the
/// detail, and at most <see cref="MaxRefusalAuditsPerMinute"/> new entries a minute on the whole port, so neither a client that
/// retries nor one that rotates its addresses pushes the history out).
/// </summary>
public sealed class McpAccess(AccessKeyStore keys, RoleStore roles, DeviceViewFactory views, AccessEpochs epochs, AuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>Calls per second per key (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsPerSecond = 20;

    /// <summary>Calls waiting for their answer at the same time per key (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsAtOnce = 4;

    /// <summary>Calls waiting for their answer at the same time on the whole MCP port, whoever calls.</summary>
    public const int CallsInPort = 32;

    /// <summary>Changes per minute per key: every tool with <c>CountsAsChange</c> (runs, task starts, calls that act outside OADM).</summary>
    public const int ChangesPerMinute = 10;

    /// <summary>File transfers (a PUT or a GET of <c>/files/</c>) at the same time per key; they never use the places of <c>/mcp</c> calls.</summary>
    public const int TransfersAtOnce = 2;

    /// <summary>File transfers at the same time on the whole MCP port, whoever transfers.</summary>
    public const int TransfersInPort = 8;

    /// <summary>Counts a change of the key; false beyond <see cref="ChangesPerMinute"/> (nothing counted then).</summary>
    public bool TryChange(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _changes.TryAcquire(caller.BindingKey);
    }

    /// <summary>One audit entry per address and refused key within this time; later refusals count into its detail.</summary>
    public static readonly TimeSpan RefusalEntry = TimeSpan.FromMinutes(1);

    /// <summary>New refusal entries a minute on the whole port; beyond it a refused key is not audited (it still counts for its address).</summary>
    public const int MaxRefusalAuditsPerMinute = 30;

    /// <summary>Addresses and keys the refusal entries remember (a flood from many addresses starts new entries).</summary>
    private const int MaxRefusalEntries = 1_000;

    /// <summary>Refused keys per address (IPv6 per /64) within <see cref="RefusedWindow"/>; then every request of it waits (like logins).</summary>
    public const int MaxRefusedPerAddress = 50;

    public static readonly TimeSpan RefusedWindow = TimeSpan.FromMinutes(5);

    private readonly AddressRateLimiter _calls = new(CallsPerSecond, total: 0, TimeSpan.FromSeconds(1), time);
    private readonly AddressRateLimiter _changes = new(ChangesPerMinute, total: 0, TimeSpan.FromMinutes(1), time);
    private readonly AddressRateLimiter _refused = new(MaxRefusedPerAddress, total: 0, RefusedWindow, time);
    private readonly AddressRateLimiter _refusalAudits = new(MaxRefusalAuditsPerMinute, total: 0, RefusalEntry, time, maxAddresses: 1);
    private readonly SemaphoreSlim _refusalGate = new(1, 1);
    private readonly Places _callPlaces = new(CallsAtOnce, CallsInPort);
    private readonly Places _transferPlaces = new(TransfersAtOnce, TransfersInPort);
    private readonly Dictionary<string, Refusals> _refusals = new(StringComparer.Ordinal);

    /// <summary>
    /// The tool caller of <paramref name="key"/>: the key itself as the caller (user id the key's id, user name and task owner
    /// "MCP &lt;key name&gt;", its role and devices of now, bound to the key's hash); null when the key is missing, unknown, deleted
    /// or ended (audited) or over its calls a second (then <see cref="McpSignIn.Throttled"/>). The call is counted as soon as the
    /// key validates, before the role and the device view are read.
    /// </summary>
    public async Task<McpSignIn> SignInAsync(string? key, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientAddress);
        var epoch = epochs.Capture(); // before the key is read: a change after it refuses the call's jobs
        var session = await keys.ValidateAsync(key, ct).ConfigureAwait(false);
        if (session is null)
        {
            _refused.TryAcquire(AddressKey(clientAddress));
            await AuditRefusedAsync(key, clientAddress, ct).ConfigureAwait(false);
            return default;
        }

        var bindingKey = ToolHandles.BindingKeyOf(session.Id, session.TokenHash);
        if (!_calls.TryAcquire(bindingKey))
        {
            return new McpSignIn(null, Throttled: true);
        }

        var permissions = await roles.PermissionsOfAsync(session.RoleId, ct).ConfigureAwait(false);
        if (session.Delegation is { } grant)
        {
            permissions = grant.Intersect(permissions);
        }

        var devices = await views.CallerDevicesAsync(session.Devices, ct).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(session.Id, session.AgentName, session.RoleId, permissions, clientAddress, string.Empty, session.TokenHash)
        {
            Devices = devices,
            AccessEpoch = epoch,
            IsAccessKey = true,
            Delegation = session.Delegation,
        };
        return new McpSignIn(new ToolCaller(caller, bindingKey)
        {
            Channel = new ToolChannel(AccessKeyStore.CleanName(session.Name), session.ExpiresUtc, CallsPerSecond, CallsAtOnce, ChangesPerMinute),
        }, Throttled: false);
    }

    /// <summary>The caller of <paramref name="key"/> (<see cref="SignInAsync"/>); null for a key that does not validate or is over its limit.</summary>
    public async Task<ToolCaller?> AuthenticateAsync(string? key, string clientAddress, CancellationToken ct) =>
        (await SignInAsync(key, clientAddress, ct).ConfigureAwait(false)).Caller;

    /// <summary>
    /// True while <paramref name="clientAddress"/> is refused before any lookup: <see cref="MaxRefusedPerAddress"/> refused keys
    /// within <see cref="RefusedWindow"/> (the login's limit per address; nothing kept beyond the window).
    /// </summary>
    public bool IsAddressLimited(string clientAddress) => _refused.IsLimited(AddressKey(clientAddress));

    private static string AddressKey(string clientAddress) =>
        System.Net.IPAddress.TryParse(clientAddress, out var address) ? AddressRateLimiter.KeyOf(address) : clientAddress;

    /// <summary>
    /// Takes one of the key's <see cref="CallsAtOnce"/> places and one of the port's <see cref="CallsInPort"/> for a call until its
    /// answer (dispose the result); null when either is taken up (nothing taken).
    /// </summary>
    public IDisposable? TryStartCall(ToolCaller caller) => _callPlaces.TryStart(caller);

    /// <summary>
    /// Takes one of the key's <see cref="TransfersAtOnce"/> places and one of the port's <see cref="TransfersInPort"/> for a file
    /// transfer until it ends (dispose the result); null when either is taken up (nothing taken). The places of <c>/mcp</c> calls
    /// stay free for the other keys while a slow transfer runs.
    /// </summary>
    public IDisposable? TryStartTransfer(ToolCaller caller) => _transferPlaces.TryStart(caller);

    /// <summary>Places taken at once per key and on the port (only keys with a place taken are kept).</summary>
    private sealed class Places(int perCaller, int inPort)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
        private int _inPort;

        public IDisposable? TryStart(ToolCaller caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            lock (_gate)
            {
                var byKey = _byKey.GetValueOrDefault(caller.BindingKey);
                if (byKey >= perCaller || _inPort >= inPort)
                {
                    return null;
                }

                _byKey[caller.BindingKey] = byKey + 1;
                _inPort++;
            }

            return new Place(this, caller.BindingKey);
        }

        private void End(string keyHash)
        {
            lock (_gate)
            {
                var now = _byKey.GetValueOrDefault(keyHash) - 1;
                if (now <= 0)
                {
                    _byKey.Remove(keyHash);
                }
                else
                {
                    _byKey[keyHash] = now;
                }

                _inPort--;
            }
        }

        private sealed class Place(Places places, string keyHash) : IDisposable
        {
            private int _ended;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _ended, 1) == 0)
                {
                    places.End(keyHash);
                }
            }
        }
    }

    private async Task AuditRefusedAsync(string? key, string clientAddress, CancellationToken ct)
    {
        var name = await keys.NameOfRefusedKeyAsync(key, ct).ConfigureAwait(false);
        var userName = name is null ? string.Empty : AccessKeyStore.AgentNameOf(name);
        var target = name is null ? (string.IsNullOrEmpty(key) ? "No access key" : "Unknown access key") : AccessKeyStore.CleanName(name);
        var slot = AddressKey(clientAddress) + "|" + userName + "|" + target; // IPv6 per /64, like the refusal count
        await _refusalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_refusals.TryGetValue(slot, out var entry) && time.GetElapsedTime(entry.Start) < RefusalEntry)
            {
                entry.Count++;
                await audit.SetDetailAsync(entry.Id, Detail(name is not null, entry.Count), ct).ConfigureAwait(false);
                return;
            }

            if (_refusals.Count >= MaxRefusalEntries)
            {
                foreach (var ended in _refusals.Where(p => time.GetElapsedTime(p.Value.Start) >= RefusalEntry).Select(p => p.Key).ToList())
                {
                    _refusals.Remove(ended);
                }

                if (_refusals.Count >= MaxRefusalEntries)
                {
                    _refusals.Clear();
                }
            }

            if (!_refusalAudits.TryAcquire("port"))
            {
                return; // the port's new entries of this minute are used up
            }

            _refusals.Remove(slot);
            if (await audit.AddAsync(userName, clientAddress, AuditActions.AccessKeyRefused, target, Detail(name is not null, 1), ct).ConfigureAwait(false) is { } id)
            {
                _refusals[slot] = new Refusals(id, time.GetTimestamp());
            }
        }
        finally
        {
            _refusalGate.Release();
        }
    }

    /// <summary>"3 refused calls"; for a key OADM still knows (it ended) "The key has ended. 3 refused calls".</summary>
    private static string Detail(bool ended, int count) =>
        (ended ? "The key has ended. " : string.Empty) + (count == 1 ? "1 refused call" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} refused calls"));

    public void Dispose() => _refusalGate.Dispose();

    /// <summary>What <see cref="SignInAsync"/> found: the caller, or nothing (a key that does not validate, or <see cref="Throttled"/>).</summary>
    public readonly record struct McpSignIn(ToolCaller? Caller, bool Throttled);

    private sealed class Refusals(long id, long start)
    {
        public long Id { get; } = id;

        public long Start { get; } = start;

        public int Count { get; set; } = 1;
    }
}
