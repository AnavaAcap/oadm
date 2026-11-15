using System.Security.Cryptography;

namespace Oadm.Server.Tools;

/// <summary>What a preview fixed for its run: the task, the devices and the payload (in memory only, like every payload).</summary>
internal sealed record PlannedRun(string PluginId, IReadOnlyList<string> DeviceIds, string? PayloadJson, string TaskName);

/// <summary>The tasks a cancel preview fixed (the ones that were waiting or running); the run asks exactly these to stop.</summary>
internal sealed record PlannedCancel(IReadOnlyList<string> TaskIds);

/// <summary>A file the caller may send once through an upload link: its name and exact size.</summary>
internal sealed record PlannedUpload(string Name, long Size);

/// <summary>A file the caller may fetch once through a download link: its name, its media type and how to make its bytes.</summary>
/// <param name="Name">The file name the answer suggests (no folder).</param>
/// <param name="ContentType">The media type of the answer.</param>
/// <param name="Open">Makes the file as the caller of the link (the same rules as in the client); its stream is closed by the channel.</param>
internal sealed record PlannedDownload(string Name, string ContentType, Func<CancellationToken, Task<Stream>> Open)
{
    /// <summary>The size in bytes when known before the file is made.</summary>
    public long? Size { get; init; }

    /// <summary>The source writes the "Downloaded file" entry itself (a task's result file): the channel writes none of its own.</summary>
    public bool AuditedBySource { get; init; }
}

/// <summary>
/// A file a tool answer offered (docs/spec/mcp.md "Files"): its reference stays good for <see cref="ToolHandles.Lifetime"/> and
/// can make several one-time links (an agent's <c>oadm_files_download_link</c>, the assistant's Save); the bytes are made only
/// when a link is fetched, with the caller's rights of then.
/// </summary>
internal sealed record OfferedFile(PlannedDownload File);

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
/// <see cref="MaxPerCaller"/> of a kind per caller, at most <see cref="MaxPerUser"/> and <see cref="MaxBytesPerUser"/> held per
/// user over all the user's keys (the oldest goes first). A binding key is "&lt;user id&gt;/&lt;key hash&gt;" (<see cref="BindingKeyOf"/>); a
/// binding key without a "/" is its own user.
/// </summary>
internal sealed class ToolHandles(TimeProvider time)
{
    /// <summary>How long a handle or a link stays good.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Open handles of one kind per caller at most.</summary>
    public const int MaxPerCaller = 20;

    /// <summary>Open handles and links of all kinds per caller (a user's sessions together; an access key is a caller of its own).</summary>
    public const int MaxPerUser = 60;

    /// <summary>
    /// What the plans of one user's open handles may hold together (the size of their JSON: the device ids and the payload; an id
    /// is about 40 bytes, so 100,000 devices are about 4 MB). When a new one does not fit, the oldest of the user go first.
    /// </summary>
    public const long MaxBytesPerUser = 16 * 1024 * 1024;

    /// <summary>The binding key of an access key or session: its user first, so the limits per user see it.</summary>
    public static string BindingKeyOf(Guid userId, string tokenHash) => userId.ToString("N") + "/" + tokenHash;

    private static string UserOf(string bindingKey)
    {
        var slash = bindingKey.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? bindingKey[..slash] : bindingKey;
    }

    private static readonly System.Text.Json.JsonSerializerOptions SizeOptions = new() { MaxDepth = 64 };

    /// <summary>The bytes a plan holds: its JSON size (a plan is records of text, numbers and lists); a download holds only a closure.</summary>
    private static long SizeOf(object value)
    {
        try
        {
            return value switch
            {
                PlannedDownload => 256,
                OfferedFile => 256,
                PlannedUpload => 256,
                PlannedChange c => 256 + System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(c.Plan, c.Plan.GetType(), SizeOptions).LongLength,
                _ => 256 + System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), SizeOptions).LongLength,
            };
        }
#pragma warning disable CA1031 // A plan that cannot be measured counts as a large one; a handle is never refused for it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return 1024 * 1024;
        }
    }

    public const string Ended = "This preview has ended or already ran. Call oadm_task_preview again.";
    public const string LinkEnded = "This upload link has ended or was used. Ask for a new one.";

    private readonly Lock _gate = new();
    private long _seq;
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

    /// <summary>A new file reference for <paramref name="bindingKey"/> (an offer of a tool answer).</summary>
    public string AddOffer(string bindingKey, PlannedDownload file) => Add(bindingKey, new OfferedFile(file));

    /// <summary>The file of <paramref name="fileRef"/>, left in place (it may make several links); null when it is unknown, ended or of another caller.</summary>
    public PlannedDownload? FindOffer(string bindingKey, string? fileRef)
    {
        if (string.IsNullOrEmpty(fileRef))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(fileRef, out var entry) && string.Equals(entry.BindingKey, bindingKey, StringComparison.Ordinal)
                && entry.Value is OfferedFile offer && entry.Ends > time.GetUtcNow() ? offer.File : null;
        }
    }

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
        var size = SizeOf(value);
        var user = UserOf(bindingKey);
        lock (_gate)
        {
            foreach (var ended in _entries.Where(e => e.Value.Ends <= now).Select(e => e.Key).ToList())
            {
                _entries.Remove(ended);
            }

            var mine = _entries.Where(e => e.Value.BindingKey == bindingKey && e.Value.Value.GetType() == value.GetType()).OrderBy(e => e.Value.Seq).ToList();
            foreach (var oldest in mine.Take(Math.Max(0, mine.Count - MaxPerCaller + 1)))
            {
                _entries.Remove(oldest.Key);
            }

            // The user's whole share, oldest first: the count and the held bytes both include the new one.
            var theirs = _entries.Where(e => e.Value.User == user).OrderBy(e => e.Value.Seq).ToList();
            var count = theirs.Count + 1;
            var bytes = theirs.Sum(e => e.Value.Size) + size;
            foreach (var oldest in theirs)
            {
                if (count <= MaxPerUser && bytes <= MaxBytesPerUser)
                {
                    break;
                }

                _entries.Remove(oldest.Key);
                count--;
                bytes -= oldest.Value.Size;
            }

            _entries[token] = new Entry(bindingKey, user, value, now + Lifetime, size, ++_seq);
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

    private sealed record Entry(string BindingKey, string User, object Value, DateTimeOffset Ends, long Size, long Seq);
}
