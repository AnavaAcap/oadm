using System.Globalization;

using Oadm.Core.Auth;

namespace Oadm.Server.LiveView;

/// <summary>
/// The audit entries of snapshots (docs/spec/mcp.md "Snapshots"): one entry per user, device and minute, the count in its detail
/// ("3 pictures"), the way refused access keys are counted. A snapshot leaves OADM, so it is audited; but a user that takes
/// pictures all day must not push every other entry out of the log (the log keeps the newest <see cref="AuditLog.MaxEntries"/>).
/// The table is bounded and its entries expire.
/// </summary>
public sealed class SnapshotAudit(AuditLog audit, TimeProvider time) : IDisposable
{
    /// <summary>How long one entry counts the pictures of a user and a device.</summary>
    public static readonly TimeSpan EntryLength = TimeSpan.FromMinutes(1);

    private const int MaxEntries = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Counted> _entries = new(StringComparer.Ordinal);

    /// <summary>Records one picture of <paramref name="target"/> (the device) as the current caller; writes an entry or raises the count of the minute's entry.</summary>
    public async Task WriteAsync(string target, string size, CancellationToken ct)
    {
        var caller = CallerContext.Current;
        var userName = caller?.AuditUserName ?? caller?.UserName ?? "system";
        var address = caller?.ClientAddress ?? string.Empty;
        var slot = userName + "|" + target;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(slot, out var entry) && time.GetElapsedTime(entry.Start) < EntryLength)
            {
                entry.Count++;
                await audit.SetDetailAsync(entry.Id, Detail(entry.Count, size), ct).ConfigureAwait(false);
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
                    _entries.Clear(); // a flood of users and devices: the next pictures start new entries (never an unbounded table)
                }
            }

            _entries.Remove(slot);
            if (await audit.AddAsync(userName, address, AuditActions.SnapshotTaken, target, Detail(1, size), ct).ConfigureAwait(false) is { } id)
            {
                _entries[slot] = new Counted(id, time.GetTimestamp());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"1280x720" for a single picture; "3 pictures" for more (the size of the latest).</summary>
    private static string Detail(int count, string size) =>
        count == 1 ? size : string.Create(CultureInfo.InvariantCulture, $"{count:N0} pictures");

    public void Dispose() => _gate.Dispose();

    private sealed class Counted(long id, long start)
    {
        public long Id { get; } = id;

        public long Start { get; } = start;

        public int Count { get; set; } = 1;
    }
}
