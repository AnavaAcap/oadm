using System.Globalization;

using Oadm.Core.Auth;
using Oadm.Sites.Transport;

namespace Oadm.Server.LiveView;

/// <summary>
/// The limit and the audit entries of snapshots (docs/spec/mcp.md "Snapshots", docs/spec/grpc.md LiveViewService.GetSnapshot), for every
/// caller of <c>GetSnapshot</c>, whether a client session or an access key. A snapshot leaves OADM, so it is audited; but a user
/// must not push every other entry out of the log (the log keeps the newest <see cref="AuditLog.MaxEntries"/>):
/// <list type="bullet">
/// <item>at most <see cref="PicturesPerMinute"/> pictures a minute per user, all sessions and keys together (<see cref="TryTake"/>;
/// the call is refused with RESOURCE_EXHAUSTED beyond it, before the camera is asked);</item>
/// <item>one audit entry per user and minute, with the count of pictures and of devices in its detail ("12 pictures of 5 devices"),
/// so the entries are bounded by the users, never by the devices. The entry holds the address and time of the minute's first picture.</item>
/// </list>
/// The tables are bounded and their entries expire.
/// </summary>
public sealed class SnapshotAudit(AuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>Camera pictures a minute per user (all their sessions and access keys together).</summary>
    public const int PicturesPerMinute = 30;

    /// <summary>The refusal text (RESOURCE_EXHAUSTED).</summary>
    public const string TooManyPictures = "Too many pictures. Wait a minute.";

    /// <summary>How long one entry counts the pictures of a user.</summary>
    public static readonly TimeSpan EntryLength = TimeSpan.FromMinutes(1);

    private const int MaxEntries = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Counted> _entries = new(StringComparer.Ordinal);
    private readonly AddressRateLimiter _limit = new(PicturesPerMinute, total: 0, EntryLength, time);

    /// <summary>Counts one picture of the user; false beyond <see cref="PicturesPerMinute"/> in the running minute (nothing counted).</summary>
    public bool TryTake(Guid userId) => _limit.TryAcquire(LimitKey(userId));

    /// <summary>True while the user has taken <see cref="PicturesPerMinute"/> pictures in the running minute (nothing counted).</summary>
    public bool IsLimited(Guid userId) => _limit.IsLimited(LimitKey(userId));

    private static string LimitKey(Guid userId) => userId.ToString("N");

    /// <summary>Records one picture of <paramref name="target"/> (the device) as the current caller; writes the user's entry of the minute or raises its counts.</summary>
    public async Task WriteAsync(string target, string size, CancellationToken ct)
    {
        var caller = CallerContext.Current;
        var userName = caller?.AuditUserName ?? caller?.UserName ?? "system";
        var address = caller?.ClientAddress ?? string.Empty;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(userName, out var entry) && time.GetElapsedTime(entry.Start) < EntryLength)
            {
                entry.Count++;
                entry.Devices.Add(target);
                await audit.SetDetailAsync(entry.Id, Detail(entry.Count, entry.Devices.Count, size), ct).ConfigureAwait(false);
                return;
            }

            if (_entries.Count >= MaxEntries)
            {
                foreach (var ended in _entries.Where(p => time.GetElapsedTime(p.Value.Start) >= EntryLength).Select(p => p.Key).ToList())
                {
                    _entries.Remove(ended);
                }

                if (_entries.Count >= MaxEntries)
                {
                    _entries.Clear(); // a flood of users: the next pictures start new entries (never an unbounded table)
                }
            }

            _entries.Remove(userName);
            if (await audit.AddAsync(userName, address, AuditActions.SnapshotTaken, target, Detail(1, 1, size), ct).ConfigureAwait(false) is { } id)
            {
                _entries[userName] = new Counted(id, time.GetTimestamp(), target);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"1280x720" for a single picture; "12 pictures of 5 devices" for more.</summary>
    private static string Detail(int pictures, int devices, string size) =>
        pictures == 1
            ? size
            : string.Create(CultureInfo.InvariantCulture, $"{pictures:N0} pictures of {devices:N0} {(devices == 1 ? "device" : "devices")}");

    public void Dispose() => _gate.Dispose();

    private sealed class Counted(long id, long start, string firstDevice)
    {
        public long Id { get; } = id;

        public long Start { get; } = start;

        public int Count { get; set; } = 1;

        /// <summary>The devices of the minute (at most <see cref="PicturesPerMinute"/>: the limit bounds it).</summary>
        public HashSet<string> Devices { get; } = new(StringComparer.Ordinal) { firstDevice };
    }
}
