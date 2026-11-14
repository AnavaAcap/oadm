using System.Security.Cryptography;

namespace Oadm.Server.Tools;

/// <summary>What a preview fixed for its run: the task, the devices and the payload (in memory only, like every payload).</summary>
internal sealed record PlannedRun(string PluginId, IReadOnlyList<string> DeviceIds, string? PayloadJson, string TaskName);

/// <summary>A file the caller may send once through an upload link: its name and exact size.</summary>
internal sealed record PlannedUpload(string Name, long Size);

/// <summary>A file the caller may fetch once through a download link: its name, its media type and how to make its bytes.</summary>
/// <param name="Name">The file name the answer suggests (no folder).</param>
/// <param name="ContentType">The media type of the answer.</param>
/// <param name="Open">Makes the file as the caller of the link (the same rules as in the client); its stream is closed by the channel.</param>
internal sealed record PlannedDownload(string Name, string ContentType, Func<CancellationToken, Task<Stream>> Open);

/// <summary>
/// What the preview of a change (devices, tags, rules, plugin tools) fixed for its run: the run tool that may take it and the
/// checked plan, in memory only. A handle is taken only by its own run tool.
/// </summary>
/// <param name="RunTool">The name of the run tool of this preview, e.g. <c>oadm_devices_remove_run</c>.</param>
/// <param name="Plan">The checked plan the run tool applies (its own record).</param>
internal sealed record PlannedChange(string RunTool, object Plan);

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
    public PlannedRun? TakeRun(string bindingKey, string? handle) => Take(bindingKey, handle, v => v is PlannedRun) as PlannedRun;

    /// <summary>The upload of <paramref name="token"/>, taken; null when it is unknown, ended or of another caller.</summary>
    public PlannedUpload? TakeUpload(string bindingKey, string? token) => Take(bindingKey, token, v => v is PlannedUpload) as PlannedUpload;

    /// <summary>A new download link token for <paramref name="bindingKey"/>.</summary>
    public string AddDownload(string bindingKey, PlannedDownload download) => Add(bindingKey, download);

    /// <summary>The download of <paramref name="token"/>, taken; null when it is unknown, ended or of another caller.</summary>
    public PlannedDownload? TakeDownload(string bindingKey, string? token) => Take(bindingKey, token, v => v is PlannedDownload) as PlannedDownload;

    /// <summary>A new handle of a change preview for <paramref name="bindingKey"/>.</summary>
    public string AddChange(string bindingKey, PlannedChange change) => Add(bindingKey, change);

    /// <summary>
    /// The plan of <paramref name="handle"/> for the run tool <paramref name="runTool"/>, taken (it never runs twice); null when it
    /// is unknown, ended, of another caller or of another run tool (then it stays for its own run tool).
    /// </summary>
    public T? TakeChange<T>(string bindingKey, string? handle, string runTool)
        where T : class =>
        (Take(bindingKey, handle, v => v is PlannedChange c && string.Equals(c.RunTool, runTool, StringComparison.Ordinal) && c.Plan is T) as PlannedChange)?.Plan as T;

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

    private object? Take(string bindingKey, string? token, Func<object, bool> kind)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(token, out var entry) || !string.Equals(entry.BindingKey, bindingKey, StringComparison.Ordinal)
                || !kind(entry.Value))
            {
                return null; // another caller's handle, or one of another kind, answers like an unknown one and stays where it is
            }

            _entries.Remove(token);
            return entry.Ends > time.GetUtcNow() ? entry.Value : null;
        }
    }

    private sealed record Entry(string BindingKey, object Value, DateTimeOffset Ends);
}
