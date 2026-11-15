using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Tools;
using Oadm.Sites.Transport;

namespace Oadm.Server.Mcp;

/// <summary>
/// Who calls the MCP server (docs/spec/mcp.md "Access keys"): the access key of <c>Authorization: Bearer</c> resolved to its
/// user with the role and device group of now (a new role or group applies to the next call; a disabled or deleted user, a
/// new password or a deleted key end it at once), at most <see cref="CallsPerSecond"/> calls per second,
/// <see cref="CallsAtOnce"/> calls waiting for their answer (every device tool reads the whole view) and
/// <see cref="ChangesPerMinute"/> changes a minute per key and, with the same numbers, per user (a user's keys share one
/// budget), at most <see cref="CallsInPort"/> calls waiting at once on the
/// whole port, file transfers in a pool of their own (<see cref="TransfersAtOnce"/> per key and user, <see cref="TransfersInPort"/> on the port),
/// a refused call counting against nothing, and every
/// refused key in the audit log (one entry per address, IPv6 per /64, and key a minute, its count in the detail, and at most
/// <see cref="MaxRefusalAuditsPerMinute"/> new entries a minute on the whole port, so neither a client that retries nor one
/// that rotates its addresses pushes the history out).
/// </summary>
public sealed class McpAccess(AuthTokenStore tokens, RoleStore roles, DeviceViewFactory views, AccessEpochs epochs, AuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>Calls per second per key and, the same number, per user (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsPerSecond = 20;

    /// <summary>Calls waiting for their answer at the same time per key and, the same number, per user (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsAtOnce = 4;

    /// <summary>Calls waiting for their answer at the same time on the whole MCP port, whoever calls.</summary>
    public const int CallsInPort = 32;

    /// <summary>Changes per minute per key and, the same number, per user: every tool with <c>CountsAsChange</c> (runs, task starts, calls that act outside OADM).</summary>
    public const int ChangesPerMinute = 10;

    /// <summary>File transfers (a PUT or a GET of <c>/files/</c>) at the same time per key and, the same number, per user; they never use the places of <c>/mcp</c> calls.</summary>
    public const int TransfersAtOnce = 2;

    /// <summary>File transfers at the same time on the whole MCP port, whoever transfers.</summary>
    public const int TransfersInPort = 8;

    /// <summary>
    /// Counts a change of the key and of its user; false beyond <see cref="ChangesPerMinute"/> for either (nothing counted for
    /// either then: both budgets are looked at before one is taken).
    /// </summary>
    public bool TryChange(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return TryBoth(_userChanges, UserKey(caller), _changes, caller.BindingKey);
    }

    /// <summary>Takes one count of each limiter, or none: a refused call charges no counter.</summary>
    private bool TryBoth(AddressRateLimiter first, string firstKey, AddressRateLimiter second, string secondKey)
    {
        lock (_budgetGate)
        {
            if (first.IsLimited(firstKey) || second.IsLimited(secondKey))
            {
                return false;
            }

            return first.TryAcquire(firstKey) && second.TryAcquire(secondKey);
        }
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
    private readonly AddressRateLimiter _userCalls = new(CallsPerSecond, total: 0, TimeSpan.FromSeconds(1), time);
    private readonly AddressRateLimiter _changes = new(ChangesPerMinute, total: 0, TimeSpan.FromMinutes(1), time);
    private readonly AddressRateLimiter _userChanges = new(ChangesPerMinute, total: 0, TimeSpan.FromMinutes(1), time);
    private readonly Lock _budgetGate = new();
    private readonly AddressRateLimiter _refused = new(MaxRefusedPerAddress, total: 0, RefusedWindow, time);
    private readonly AddressRateLimiter _refusalAudits = new(MaxRefusalAuditsPerMinute, total: 0, RefusalEntry, time, maxAddresses: 1);
    private readonly SemaphoreSlim _refusalGate = new(1, 1);
    private readonly Places _callPlaces = new(CallsAtOnce, CallsInPort);
    private readonly Places _transferPlaces = new(TransfersAtOnce, TransfersInPort);
    private readonly Dictionary<string, Refusals> _refusals = new(StringComparer.Ordinal);

    /// <summary>The answer to a change with a Read only key.</summary>
    public const string ReadOnlyKey = "This access key is Read only.";

    /// <summary>The client machine of an agent's calls before its key name: a task's owner reads "admin@AI agent Laptop".</summary>
    public const string AgentMachinePrefix = "AI agent ";

    /// <summary>
    /// The tool caller of <paramref name="key"/> (its user with the role and devices of now, client machine "AI agent &lt;key name&gt;",
    /// task owner "&lt;user&gt;@AI agent &lt;key name&gt;", bound to the key's hash and its user); null when the key is missing, unknown,
    /// ended, its user is disabled (audited) or the key or its user is over its calls a second (then <see cref="McpSignIn.Throttled"/>).
    /// The call is counted as soon as the key validates, before the role and the device view are read.
    /// </summary>
    public async Task<McpSignIn> SignInAsync(string? key, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientAddress);
        var epoch = epochs.Capture(); // before the key is read: a change after it refuses the call's jobs
        var session = await tokens.ValidateAccessKeyAsync(key, ct).ConfigureAwait(false);
        if (session?.AccessKeyName is not { } rawName)
        {
            _refused.TryAcquire(AddressKey(clientAddress));
            await AuditRefusedAsync(key, clientAddress, ct).ConfigureAwait(false);
            return default;
        }

        var bindingKey = ToolHandles.BindingKeyOf(session.UserId, session.TokenHash);
        if (!TryBoth(_userCalls, session.UserId.ToString("N"), _calls, bindingKey))
        {
            return new McpSignIn(null, Throttled: true);
        }

        var keyName = AuthTokenStore.CleanAccessKeyName(rawName); // keys made before the name rule may hold anything
        var permissions = await roles.PermissionsOfAsync(session.RoleId, ct).ConfigureAwait(false);
        var devices = await views.CallerDevicesAsync(session.DeviceAccess, ct).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(session.UserId, session.UserName, session.RoleId, permissions, clientAddress, AgentMachinePrefix + keyName, session.TokenHash)
        {
            Devices = devices,
            AccessEpoch = epoch,
            AuditUserName = AuditActions.ViaAccessKey(session.UserName, keyName), // every change an agent causes
            ReadOnly = session.ReadOnly,
        };
        return new McpSignIn(new ToolCaller(caller, bindingKey)
        {
            ReadOnly = session.ReadOnly,
            ReadOnlyRefusal = ReadOnlyKey,
            Channel = new ToolChannel(keyName, session.ExpiresUtc, CallsPerSecond, CallsAtOnce, ChangesPerMinute),
            ChannelPermission = HostPermissions.McpUse, // access keys are MCP's: their user also needs "Use AI agents"
        }, Throttled: false);
    }

    /// <summary>The caller of <paramref name="key"/> (<see cref="SignInAsync"/>); null for a key that does not validate or is over its limit.</summary>
    public async Task<ToolCaller?> AuthenticateAsync(string? key, string clientAddress, CancellationToken ct) =>
        (await SignInAsync(key, clientAddress, ct).ConfigureAwait(false)).Caller;

    /// <summary>
    /// Why the user of <paramref name="caller"/> may not use the MCP server (the role lacks "Use AI agents": the files, the skill
    /// and every other route are shut like the tools); null when it may. The text is the one of the tool list and the client.
    /// </summary>
    public string? ChannelRefusal(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return caller.ChannelPermission is { } permission && !caller.User.Has(permission) ? roles.Catalog.DeniedMessage(permission) : null;
    }

    /// <summary>
    /// True while <paramref name="clientAddress"/> is refused before any lookup: <see cref="MaxRefusedPerAddress"/> refused keys
    /// within <see cref="RefusedWindow"/> (the login's limit per address; nothing kept beyond the window).
    /// </summary>
    public bool IsAddressLimited(string clientAddress) => _refused.IsLimited(AddressKey(clientAddress));

    private static string AddressKey(string clientAddress) =>
        System.Net.IPAddress.TryParse(clientAddress, out var address) ? AddressRateLimiter.KeyOf(address) : clientAddress;

    /// <summary>
    /// Takes one of the key's <see cref="CallsAtOnce"/> places, one of its user's and one of the port's <see cref="CallsInPort"/> for
    /// a call until its answer (dispose the result); null when any is taken up (nothing taken).
    /// </summary>
    public IDisposable? TryStartCall(ToolCaller caller) => _callPlaces.TryStart(caller);

    /// <summary>
    /// Takes one of the key's <see cref="TransfersAtOnce"/> places, one of its user's and one of the port's
    /// <see cref="TransfersInPort"/> for a file transfer until it ends (dispose the result); null when any is taken up (nothing
    /// taken). The places of <c>/mcp</c> calls stay free for the other users while a slow transfer runs.
    /// </summary>
    public IDisposable? TryStartTransfer(ToolCaller caller) => _transferPlaces.TryStart(caller);

    private static string UserKey(ToolCaller caller) => caller.User.UserId.ToString("N");

    /// <summary>Places taken at once per key, per user and on the port (only keys and users with a place taken are kept).</summary>
    private sealed class Places(int perCaller, int inPort)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _byUser = new(StringComparer.Ordinal);
        private int _inPort;

        public IDisposable? TryStart(ToolCaller caller)
        {
            ArgumentNullException.ThrowIfNull(caller);
            var user = UserKey(caller);
            lock (_gate)
            {
                var byKey = _byKey.GetValueOrDefault(caller.BindingKey);
                var byUser = _byUser.GetValueOrDefault(user);
                if (byKey >= perCaller || byUser >= perCaller || _inPort >= inPort)
                {
                    return null;
                }

                _byKey[caller.BindingKey] = byKey + 1;
                _byUser[user] = byUser + 1;
                _inPort++;
            }

            return new Place(this, caller.BindingKey, user);
        }

        private void End(string keyHash, string user)
        {
            lock (_gate)
            {
                Release(_byKey, keyHash);
                Release(_byUser, user);
                _inPort--;
            }

            static void Release(Dictionary<string, int> counts, string id)
            {
                var now = counts.GetValueOrDefault(id) - 1;
                if (now <= 0)
                {
                    counts.Remove(id);
                }
                else
                {
                    counts[id] = now;
                }
            }
        }

        private sealed class Place(Places places, string keyHash, string user) : IDisposable
        {
            private int _ended;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _ended, 1) == 0)
                {
                    places.End(keyHash, user);
                }
            }
        }
    }

    private async Task AuditRefusedAsync(string? key, string clientAddress, CancellationToken ct)
    {
        var owner = await tokens.OwnerOfRefusedKeyAsync(key, ct).ConfigureAwait(false);
        var userName = owner is null ? string.Empty : AuditActions.ViaAccessKey(owner.UserName, AuthTokenStore.CleanAccessKeyName(owner.Name));
        var target = owner is null ? null : AuthTokenStore.CleanAccessKeyName(owner.Name);
        target ??= (string.IsNullOrEmpty(key) ? "No access key" : "Unknown access key");
        var slot = AddressKey(clientAddress) + "|" + userName + "|" + target; // IPv6 per /64, like the refusal count
        await _refusalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_refusals.TryGetValue(slot, out var entry) && time.GetElapsedTime(entry.Start) < RefusalEntry)
            {
                entry.Count++;
                await audit.SetDetailAsync(entry.Id, Detail(owner is not null, entry.Count), ct).ConfigureAwait(false);
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
            if (await audit.AddAsync(userName, clientAddress, AuditActions.AccessKeyRefused, target, Detail(owner is not null, 1), ct).ConfigureAwait(false) is { } id)
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
