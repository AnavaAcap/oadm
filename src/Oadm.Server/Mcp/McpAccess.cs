using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Tools;
using Oadm.Sites.Transport;

namespace Oadm.Server.Mcp;

/// <summary>
/// Who calls the MCP server (docs/spec/mcp.md "Access keys"): the access key of <c>Authorization: Bearer</c> resolved to its
/// user with the role and device group of now (a new role or group applies to the next call; a disabled or deleted user, a
/// new password or a deleted key end it at once), at most <see cref="CallsPerSecond"/> calls per second and
/// <see cref="CallsAtOnce"/> calls waiting for their answer per key (every device tool reads the whole view), and every
/// refused key in the audit log (one entry per address and key a minute, its count in the detail, so a client that retries
/// never pushes the history out).
/// </summary>
public sealed class McpAccess(AuthTokenStore tokens, RoleStore roles, DeviceViewFactory views, AccessEpochs epochs, AuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>Calls per second and key (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsPerSecond = 20;

    /// <summary>Counts a task run of the key; false beyond <see cref="RunsPerMinute"/> (nothing counted).</summary>
    public bool TryRun(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _runs.TryAcquire(caller.BindingKey);
    }

    /// <summary>Calls of one key that wait for their answer at the same time (HTTP 429 with Retry-After beyond).</summary>
    public const int CallsAtOnce = 4;

    /// <summary>Task runs per minute and key (stage 2: the task tools).</summary>
    public const int RunsPerMinute = 10;

    /// <summary>One audit entry per address and refused key within this time; later refusals count into its detail.</summary>
    public static readonly TimeSpan RefusalEntry = TimeSpan.FromMinutes(1);

    /// <summary>Addresses and keys the refusal entries remember (a flood from many addresses starts new entries).</summary>
    private const int MaxRefusalEntries = 1_000;

    /// <summary>Refused keys per address (IPv6 per /64) within <see cref="RefusedWindow"/>; then every request of it waits (like logins).</summary>
    public const int MaxRefusedPerAddress = 50;

    public static readonly TimeSpan RefusedWindow = TimeSpan.FromMinutes(5);

    private readonly AddressRateLimiter _calls = new(CallsPerSecond, total: 0, TimeSpan.FromSeconds(1), time);
    private readonly AddressRateLimiter _runs = new(RunsPerMinute, total: 0, TimeSpan.FromMinutes(1), time);
    private readonly AddressRateLimiter _refused = new(MaxRefusedPerAddress, total: 0, RefusedWindow, time);
    private readonly SemaphoreSlim _refusalGate = new(1, 1);
    private readonly Lock _inFlightGate = new();
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal); // only keys with a call that waits
    private readonly Dictionary<string, Refusals> _refusals = new(StringComparer.Ordinal);

    /// <summary>The answer to a change with a Read only key.</summary>
    public const string ReadOnlyKey = "This access key is Read only.";

    /// <summary>
    /// The tool caller of <paramref name="key"/> (its user with the role and devices of now, client machine "MCP &lt;key name&gt;",
    /// task owner "&lt;user&gt;@MCP &lt;key name&gt;", bound to the key's hash); null when the key is missing, unknown, ended or its
    /// user is disabled (audited).
    /// </summary>
    public async Task<ToolCaller?> AuthenticateAsync(string? key, string clientAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clientAddress);
        var epoch = epochs.Capture(); // before the key is read: a change after it refuses the call's jobs
        var session = await tokens.ValidateAccessKeyAsync(key, ct).ConfigureAwait(false);
        if (session?.AccessKeyName is not { } keyName)
        {
            _refused.TryAcquire(AddressKey(clientAddress));
            await AuditRefusedAsync(key, clientAddress, ct).ConfigureAwait(false);
            return null;
        }

        var permissions = await roles.PermissionsOfAsync(session.RoleId, ct).ConfigureAwait(false);
        var devices = await views.CallerDevicesAsync(session.DeviceAccess, ct).ConfigureAwait(false);
        var caller = new AuthenticatedCaller(session.UserId, session.UserName, session.RoleId, permissions, clientAddress, "MCP " + keyName, session.TokenHash)
        {
            Devices = devices,
            AccessEpoch = epoch,
            AuditUserName = AuditActions.ViaAccessKey(session.UserName, keyName), // every change an agent causes
        };
        return new ToolCaller(caller, session.TokenHash)
        {
            ReadOnly = session.ReadOnly,
            ReadOnlyRefusal = ReadOnlyKey,
            Channel = new ToolChannel(keyName, session.ExpiresUtc, CallsPerSecond, CallsAtOnce, RunsPerMinute),
            ChannelPermission = HostPermissions.McpUse, // access keys are MCP's: their user also needs "Use AI agents"
        };
    }

    /// <summary>
    /// True while <paramref name="clientAddress"/> is refused before any lookup: <see cref="MaxRefusedPerAddress"/> refused keys
    /// within <see cref="RefusedWindow"/> (the login's limit per address; nothing kept beyond the window).
    /// </summary>
    public bool IsAddressLimited(string clientAddress) => _refused.IsLimited(AddressKey(clientAddress));

    private static string AddressKey(string clientAddress) =>
        System.Net.IPAddress.TryParse(clientAddress, out var address) ? AddressRateLimiter.KeyOf(address) : clientAddress;

    /// <summary>Counts a call of the key; false beyond <see cref="CallsPerSecond"/> (nothing counted).</summary>
    public bool TryCall(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        return _calls.TryAcquire(caller.BindingKey);
    }

    /// <summary>
    /// Takes one of the key's <see cref="CallsAtOnce"/> places for a call until its answer (dispose the result); null when all
    /// are taken (nothing taken).
    /// </summary>
    public IDisposable? TryStartCall(ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        lock (_inFlightGate)
        {
            var now = _inFlight.GetValueOrDefault(caller.BindingKey);
            if (now >= CallsAtOnce)
            {
                return null;
            }

            _inFlight[caller.BindingKey] = now + 1;
        }

        return new CallInFlight(this, caller.BindingKey);
    }

    private void EndCall(string keyHash)
    {
        lock (_inFlightGate)
        {
            var now = _inFlight.GetValueOrDefault(keyHash) - 1;
            if (now <= 0)
            {
                _inFlight.Remove(keyHash);
            }
            else
            {
                _inFlight[keyHash] = now;
            }
        }
    }

    private sealed class CallInFlight(McpAccess access, string keyHash) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                access.EndCall(keyHash);
            }
        }
    }

    private async Task AuditRefusedAsync(string? key, string clientAddress, CancellationToken ct)
    {
        var owner = await tokens.OwnerOfRefusedKeyAsync(key, ct).ConfigureAwait(false);
        var userName = owner is null ? string.Empty : AuditActions.ViaAccessKey(owner.UserName, owner.Name);
        var target = owner?.Name ?? (string.IsNullOrEmpty(key) ? "No access key" : "Unknown access key");
        var slot = clientAddress + "|" + userName + "|" + target;
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

    private sealed class Refusals(long id, long start)
    {
        public long Id { get; } = id;

        public long Start { get; } = start;

        public int Count { get; set; } = 1;
    }
}
