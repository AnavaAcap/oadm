using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tasks;

/// <summary>The result file of one task (<see cref="ITaskExecutionContext.SaveResultFileAsync"/>): its name, type, size and owner.</summary>
/// <param name="TaskId">The task.</param>
/// <param name="FileName">The name the user gets (no folder).</param>
/// <param name="ContentType">Its media type.</param>
/// <param name="Size">Bytes.</param>
/// <param name="OwnerId">The user (or access key) who started the task; only that caller and administrators fetch it.</param>
/// <param name="SavedUtc">When the task kept it.</param>
public sealed record TaskResultFileInfo(Guid TaskId, string FileName, string ContentType, long Size, Guid OwnerId, DateTimeOffset SavedUtc);

/// <summary>
/// The result files of tasks (docs/spec/mcp.md "Files"): one file per task in <c>&lt;datafolder&gt;/task-results</c>, kept for
/// <see cref="Retention"/> or until the server restarts (the index lives in memory; the folder is emptied at start). At most
/// <see cref="TaskResultFile.MaxBytes"/> per file and <see cref="QuotaBytes"/> for all, the oldest go first. Only tasks a user
/// or an access key started keep one: the owner fetches it (and administrators), through <c>TaskService.DownloadResult</c>.
/// </summary>
public sealed class TaskResultFiles
{
    /// <summary>How long a result file is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>What all result files together may use (1 GB).</summary>
    public const long QuotaBytes = 1024L * 1024 * 1024;

    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, TaskResultFileInfo> _files = [];
    private bool _cleared;

    public TaskResultFiles(OadmPaths paths, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.DataDirectory, "task-results");
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The largest file; <see cref="TaskResultFile.MaxBytes"/> unless a test sets less.</summary>
    public long MaxBytes { get; init; } = TaskResultFile.MaxBytes;

    /// <summary>The result file of <paramref name="taskId"/>; null when it has none (or it ended).</summary>
    public TaskResultFileInfo? Find(Guid taskId)
    {
        lock (_gate)
        {
            DropEnded();
            return _files.GetValueOrDefault(taskId);
        }
    }

    /// <summary>Keeps <paramref name="content"/> as the result file of <paramref name="taskId"/> (a second one replaces the first).</summary>
    /// <exception cref="InvalidOperationException">The file is larger than <see cref="TaskResultFile.MaxBytes"/>.</exception>
    public async Task<TaskResultFileInfo> SaveAsync(Guid taskId, Guid ownerId, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var name = SafeName(fileName);
        var type = string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100 || contentType.Any(char.IsControl) ? "application/octet-stream" : contentType.Trim();
        PrepareFolder();
        var path = PathOf(taskId);
        var part = path + ".part";
        long size = 0;
        try
        {
            var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > MaxBytes)
                    {
                        throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The result file is larger than {MaxBytes / (1024 * 1024)} MB."));
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            File.Move(part, path, overwrite: true);
        }
        finally
        {
            File.Delete(part);
        }

        var info = new TaskResultFileInfo(taskId, name, type, size, ownerId, _time.GetUtcNow());
        lock (_gate)
        {
            _files[taskId] = info;
            DropEnded();
            // The oldest go first while all of them together are over the quota (the new one stays).
            var total = _files.Values.Sum(f => f.Size);
            foreach (var oldest in _files.Values.Where(f => f.TaskId != taskId).OrderBy(f => f.SavedUtc).ToList())
            {
                if (total <= QuotaBytes)
                {
                    break;
                }

                Forget(oldest.TaskId);
                total -= oldest.Size;
            }
        }

        return info;
    }

    /// <summary>Opens the result file of <paramref name="taskId"/> for reading; null when it has none.</summary>
    public Stream? Open(Guid taskId)
    {
        if (Find(taskId) is null)
        {
            return null;
        }

        try
        {
            return new FileStream(PathOf(taskId), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Forgets the result files of deleted tasks.</summary>
    public void Delete(IEnumerable<Guid> taskIds)
    {
        ArgumentNullException.ThrowIfNull(taskIds);
        lock (_gate)
        {
            foreach (var id in taskIds)
            {
                Forget(id);
            }
        }
    }

    private string PathOf(Guid taskId) => Path.Combine(_directory, taskId.ToString("N") + ".bin");

    /// <summary>The folder exists and holds nothing of an earlier start (the index of those files is gone).</summary>
    private void PrepareFolder()
    {
        lock (_gate)
        {
            if (_cleared)
            {
                return;
            }

            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // held by another process for a moment: left for the next start
                    }
                }
            }

            Directory.CreateDirectory(_directory);
            _cleared = true;
        }
    }

    private void DropEnded()
    {
        var now = _time.GetUtcNow();
        foreach (var ended in _files.Values.Where(f => now - f.SavedUtc >= Retention).Select(f => f.TaskId).ToList())
        {
            Forget(ended);
        }
    }

    private void Forget(Guid taskId)
    {
        if (_files.Remove(taskId))
        {
            try
            {
                File.Delete(PathOf(taskId));
            }
            catch (IOException)
            {
                // an open download keeps it a moment longer (FileShare.Delete on Windows); the next start removes it
            }
        }
    }

    /// <summary>A file name without a folder or control characters, at most <see cref="TaskResultFile.MaxNameLength"/> characters.</summary>
    internal static string SafeName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/').Split('/').Last()).Trim();
        name = new string([.. name.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0 && c is not ('/' or '\\' or ':' or '"'))]);
        if (name.Length == 0 || name.All(c => c == '.'))
        {
            name = "result";
        }

        return name.Length > TaskResultFile.MaxNameLength ? name[..TaskResultFile.MaxNameLength] : name;
    }
}
