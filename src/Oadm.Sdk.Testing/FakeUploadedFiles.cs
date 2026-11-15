using System.Collections.Concurrent;
using System.Security.Cryptography;

using Oadm.Sdk.Plugins;

namespace Oadm.Sdk.Testing;

/// <summary>
/// Uploaded files in memory (<see cref="IUploadedFiles"/>): <see cref="Add"/> puts a file in and answers its id, as an upload
/// link or the assistant's Attach file would. For <see cref="FakeCallContext.Uploads"/> and a task's <c>UploadedFiles</c>.
/// </summary>
public sealed class FakeUploadedFiles : IUploadedFiles
{
    private readonly ConcurrentDictionary<string, (UploadedFile File, byte[] Content)> _files = new(StringComparer.Ordinal);

    /// <summary>Adds a file and answers it (a 32 character lower case hex id, the SHA-256 in upper case hex).</summary>
    public UploadedFile Add(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(content);
        var file = new UploadedFile(Guid.NewGuid().ToString("N"), name, content.Length, Convert.ToHexString(SHA256.HashData(content)));
        _files[file.Id] = (file, content.ToArray());
        return file;
    }

    /// <inheritdoc />
    public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) =>
        Task.FromResult(fileId is not null && _files.TryGetValue(fileId, out var entry) ? entry.File : null);

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct) =>
        fileId is not null && _files.TryGetValue(fileId, out var entry)
            ? Task.FromResult<Stream>(new MemoryStream(entry.Content, writable: false))
            : throw new FileNotFoundException("No uploaded file has this id.");
}
