using System.Security.Cryptography;

namespace Oadm.Server.Tools;

/// <summary>What a preview fixed for its run: the task, the devices and the payload (in memory only, like every payload).</summary>
internal sealed record PlannedRun(string PluginId, IReadOnlyList<string> DeviceIds, string? PayloadJson, string TaskName);

/// <summary>A file the caller may send once through an upload link: its name and exact size.</summary>
internal sealed record PlannedUpload(string Name, long Size);

/// <summary>
/// The handles of previews and the upload links (docs/spec/mcp.md "Tasks", "Files"): each one random, bound to the caller's
/// binding key, good for <see cref="Lifetime"/> and taken once. Kept in server memory only (a restart ends them), at most
/// <see cref="MaxPerCaller"/> of a kind per caller (the oldest goes first).
/// </summary>
internal sealed class ToolHandles(TimeProvider time)
{
    /// <summary>How long a handle or a link stays good.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Open handles of one kind per caller at most.</summary>
    public const int MaxPerCaller = 20;

    public const string Ended = "This preview has ended or already ran. Call oadm_task_preview again.";
    public const string LinkEnded = "This upload link has ended or was used. Ask for a new one.";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>A new handle of <paramref name="run"/> for <paramref name="bindingKey"/>.</summary>
    public string AddRun(string bindingKey, PlannedRun run) => Add(bindingKey, run);

    /// <summary>A new upload link token for <paramref name="bindingKey"/>.</summary>
    public string AddUpload(string bindingKey, PlannedUpload upload) => Add(bindingKey, upload);

    /// <summary>The run of <paramref name="handle"/>, taken (it never runs twice); null when it is unknown, ended or of another caller.</summary>
    public PlannedRun? TakeRun(string bindingKey, string? handle) => Take(bindingKey, handle) as PlannedRun;

    /// <summary>The upload of <paramref name="token"/>, taken; null when it is unknown, ended or of another caller.</summary>
    public PlannedUpload? TakeUpload(string bindingKey, string? token) => Take(bindingKey, token) as PlannedUpload;

    /// <summary>When a handle made now ends.</summary>
    public DateTimeOffset EndOfNew() => time.GetUtcNow() + Lifetime;

    private string Add(string bindingKey, object value)
    {
        ArgumentNullException.ThrowIfNull(bindingKey);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = time.GetUtcNow();
        lock (_gate)
        {
            foreach (var ended in _entries.Where(e => e.Value.Ends <= now).Select(e => e.Key).ToList())
            {
                _entries.Remove(ended);
            }

            var mine = _entries.Where(e => e.Value.BindingKey == bindingKey && e.Value.Value.GetType() == value.GetType()).OrderBy(e => e.Value.Ends).ToList();
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - MaxPerCaller + 1)))
            {
                _entries.Remove(oldest.Key);
            }

            _entries[token] = new Entry(bindingKey, value, now + Lifetime);
        }

        return token;
    }

    private object? Take(string bindingKey, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var entry) || !string.Equals(entry.BindingKey, bindingKey, StringComparison.Ordinal))
            {
                return null; // another caller's handle answers like an unknown one and stays theirs
            }

            _entries.Remove(token);
            return entry.Ends > time.GetUtcNow() ? entry.Value : null;
        }
    }

    private sealed record Entry(string BindingKey, object Value, DateTimeOffset Ends);
}
