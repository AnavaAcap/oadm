using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Oadm.Plugins.Acap;

/// <summary>
/// The rules of a vendor's ZIP of packages, the same in the client (<see cref="EapPackageFiles"/>, unpacked to a private temp
/// folder) and on the server (<see cref="ReadAsync"/> for agents and the Assistant, read as streams, never written to disk):
/// only the .eap entries (also in subfolders, never the resource copies of macOS) and one <c>oadm-packages.json</c> at the top
/// level or next to the .eap files; at most <see cref="MaxEntries"/> entries, <see cref="AcapPayload.MaxFiles"/> packages,
/// <see cref="MaxBytes"/> unpacked (declared sizes first, then counted while reading), an entry that unpacks to more than
/// <see cref="MaxRatio"/> times its packed size refused (an .eap is compressed already); no entry name with an absolute path
/// or "..", no two packages of the same name, no password. A refusal is an <see cref="InvalidEapException"/> with one plain
/// sentence.
/// </summary>
public static class EapZip
{
    /// <summary>Largest unpacked total: the default size limit of one upload (<c>ServerSettings.DefaultUploadsMaxMegabytes</c>).</summary>
    public const long MaxBytes = 2048L * 1024 * 1024;

    /// <summary>A ZIP with more entries than this is refused: no package of one application needs them.</summary>
    public const int MaxEntries = 10_000;

    /// <summary>An entry that unpacks to more than this many times its packed size is refused (a ZIP bomb; .eap files hardly shrink).</summary>
    public const int MaxRatio = 100;

    private const string Eap = ".eap";

    /// <summary>The entries of a pick from one ZIP.</summary>
    /// <param name="Packages">The .eap entries in ZIP order.</param>
    /// <param name="Rules">The mapping file, null without one.</param>
    /// <param name="Declared">The declared unpacked size of the packages.</param>
    public sealed record Selection(IReadOnlyList<ZipArchiveEntry> Packages, ZipArchiveEntry? Rules, long Declared);

    /// <summary>The name of an entry without its folders ("lpr_2.4_aarch64.eap").</summary>
    public static string NameOf(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = entry.FullName.Replace('\\', '/');
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>
    /// An entry that holds a package: a file ending in .eap, except the resource copies macOS adds to a ZIP
    /// (<c>__MACOSX/._app.eap</c>), which only look like packages.
    /// </summary>
    public static bool IsPackageEntry(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        var segments = entryName.Replace('\\', '/').Split('/');
        var name = segments[^1];
        return name.EndsWith(Eap, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("._", StringComparison.Ordinal)
            && !segments.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An entry name a file system could read as a place outside the ZIP: absolute, a drive, or a ".." segment.</summary>
    public static bool IsUnsafeName(string entryName)
    {
        ArgumentNullException.ThrowIfNull(entryName);
        var path = entryName.Replace('\\', '/');
        return path.StartsWith('/') || (path.Length >= 2 && path[1] == ':') || path.Split('/').Any(s => s == "..");
    }

    /// <summary>
    /// The entries of the ZIP to use, checked before anything is unpacked. <paramref name="already"/> is the declared size of
    /// packages of the same pick before this ZIP.
    /// </summary>
    public static Selection Select(ZipArchive zip, string zipName, long maxBytes = MaxBytes, int maxEntries = MaxEntries, long already = 0)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentNullException.ThrowIfNull(zipName);
        if (zip.Entries.Count > maxEntries)
        {
            throw new InvalidEapException($"{zipName} has too many files.");
        }

        var packages = new List<ZipArchiveEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<ZipArchiveEntry>();
        var declared = already;
        foreach (var entry in zip.Entries)
        {
            if (IsUnsafeName(entry.FullName))
            {
                throw new InvalidEapException($"{zipName} has a file with a path outside the ZIP.");
            }

            if (IsRulesEntry(entry.FullName))
            {
                rules.Add(entry);
                continue;
            }

            if (!IsPackageEntry(entry.FullName))
            {
                continue;
            }

            if (entry.IsEncrypted)
            {
                throw new InvalidEapException($"{zipName} is password protected.");
            }

            if (entry.Length > maxBytes - declared || (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio) || (entry.CompressedLength == 0 && entry.Length > 0))
            {
                throw new InvalidEapException(TooLarge(zipName));
            }

            if (!names.Add(NameOf(entry)))
            {
                throw new InvalidEapException($"{zipName} has two files named {NameOf(entry)}.");
            }

            declared += entry.Length;
            packages.Add(entry);
        }

        if (packages.Count == 0)
        {
            throw new InvalidEapException($"{zipName} has no .eap files.");
        }

        if (packages.Count > AcapPayload.MaxFiles)
        {
            throw new InvalidEapException(AcapCompatibility.TooManyFiles);
        }

        // The mapping file counts at the top level or in a folder that holds packages; elsewhere it is ignored.
        var folders = packages.Select(FolderOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = rules.Where(r => FolderOf(r).Length == 0 || folders.Contains(FolderOf(r))).ToList();
        if (used.Count > 1)
        {
            throw new InvalidEapException($"{zipName} has more than one {AcapPackageRules.FileName}.");
        }

        if (used.Count == 1 && (used[0].IsEncrypted || used[0].Length > AcapPackageRules.MaxBytes))
        {
            throw new InvalidEapException(used[0].IsEncrypted ? $"{zipName} is password protected." : $"{AcapPackageRules.FileName} is larger than 64 KB.");
        }

        return new Selection(packages, used.Count == 1 ? used[0] : null, declared - already);
    }

    /// <summary>"app.zip is too large to unpack."</summary>
    public static string TooLarge(string zipName) => $"{zipName} is too large to unpack.";

    /// <summary>"app.zip is damaged or not a ZIP file."</summary>
    public static string Broken(string zipName) => $"{zipName} is damaged or not a ZIP file.";

    /// <summary>
    /// The entry's content, refused beyond its declared size or <paramref name="budget"/> (the bytes still allowed for the whole
    /// pick, counted down) and when it ends short of its declared size.
    /// </summary>
    public static async Task<Stream> OpenAsync(ZipArchiveEntry entry, string zipName, StrongBox<long> budget, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(budget);
        var source = await entry.OpenAsync(ct).ConfigureAwait(false);
        return new BoundedStream(source, entry.Length, budget, zipName);
    }

    /// <summary>The mapping file's text, at most 64 KB however large the ZIP says it is.</summary>
    public static async Task<byte[]> ReadRulesAsync(ZipArchiveEntry entry, string zipName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var stream = await OpenAsync(entry, zipName, new StrongBox<long>(AcapPackageRules.MaxBytes), ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct).ConfigureAwait(false);
            return copy.ToArray();
        }
    }

    /// <summary>One build of a ZIP the server read: its name, its entry, its size, its SHA-256 (upper hex) and its manifest.</summary>
    public sealed record ZipBuild(string Name, string EntryName, long Size, string Sha256, EapManifest Package);

    /// <summary>What the server read from a ZIP: the builds and the rows of its mapping file.</summary>
    public sealed record ZipContents(IReadOnlyList<ZipBuild> Builds, IReadOnlyList<AcapPackageRule> Rules, bool HasRules);

    /// <summary>
    /// Reads an uploaded ZIP on the server (agents, the Assistant): every .eap entry through the limits above, its manifest and
    /// its SHA-256 while it streams, and the mapping file. Nothing is written to disk. The stream must be seekable (an upload).
    /// </summary>
    public static async Task<ZipContents> ReadAsync(Stream zipStream, string zipName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        try
        {
            if (!zipStream.CanSeek)
            {
                throw new InvalidEapException(Broken(zipName));
            }

            var zip = await ZipArchive.CreateAsync(zipStream, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct).ConfigureAwait(false);
            await using (zip.ConfigureAwait(false))
            {
                var selection = Select(zip, zipName);
                var budget = new StrongBox<long>(MaxBytes);
                var builds = new List<ZipBuild>(selection.Packages.Count);
                foreach (var entry in selection.Packages)
                {
                    var name = NameOf(entry);
                    var stream = await OpenAsync(entry, zipName, budget, ct).ConfigureAwait(false);
                    await using (stream.ConfigureAwait(false))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var hashing = new HashingStream(stream, hash);
                        EapManifest package;
                        try
                        {
                            package = await EapReader.ReadAsync(hashing, ct).ConfigureAwait(false);
                        }
                        catch (InvalidEapException ex)
                        {
                            throw new InvalidEapException(name + ": " + ex.Message, ex);
                        }

                        await hashing.CopyToAsync(Stream.Null, ct).ConfigureAwait(false);
                        builds.Add(new ZipBuild(name, entry.FullName, entry.Length, Convert.ToHexString(hash.GetHashAndReset()), package));
                    }
                }

                IReadOnlyList<AcapPackageRule> rules = [];
                if (selection.Rules is { } rulesEntry)
                {
                    rules = AcapPackageRules.Parse(await ReadRulesAsync(rulesEntry, zipName, ct).ConfigureAwait(false), [.. builds.Select(b => b.Name)]);
                }

                return new ZipContents(builds, rules, selection.Rules is not null);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidEapException(Broken(zipName), ex);
        }
    }

    /// <summary>
    /// Opens one build of an uploaded ZIP for the upload to the device: the entry by its full name, within its declared size.
    /// The returned stream owns the archive.
    /// </summary>
    public static async Task<Stream> OpenBuildAsync(Stream zipStream, string zipName, string entryName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(zipStream);
        try
        {
            if (!zipStream.CanSeek)
            {
                throw new InvalidEapException(Broken(zipName));
            }

            var zip = await ZipArchive.CreateAsync(zipStream, ZipArchiveMode.Read, leaveOpen: false, entryNameEncoding: null, ct).ConfigureAwait(false);
            try
            {
                var selection = Select(zip, zipName);
                var entry = selection.Packages.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.Ordinal))
                    ?? throw new InvalidEapException($"{zipName} no longer holds {entryName}.");
                var stream = await OpenAsync(entry, zipName, new StrongBox<long>(MaxBytes), ct).ConfigureAwait(false);
                return new OwningStream(stream, zip);
            }
            catch
            {
                await zip.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
        {
            throw new InvalidEapException(Broken(zipName), ex);
        }
    }

    private static bool IsRulesEntry(string entryName)
    {
        var segments = entryName.Replace('\\', '/').Split('/');
        return string.Equals(segments[^1], AcapPackageRules.FileName, StringComparison.OrdinalIgnoreCase)
            && !segments.Contains("__MACOSX", StringComparer.OrdinalIgnoreCase);
    }

    private static string FolderOf(ZipArchiveEntry entry)
    {
        var path = entry.FullName.Replace('\\', '/');
        var cut = path.LastIndexOf('/');
        return cut < 0 ? string.Empty : path[..cut];
    }

    /// <summary>Reads at most the entry's declared size and the shared budget; ending short of the declared size is a damaged ZIP.</summary>
    private sealed class BoundedStream(Stream inner, long declared, StrongBox<long> budget, string zipName) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => declared;

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count), count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false), buffer.Length);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int Count(int read, int asked)
        {
            _read += read;
            budget.Value -= read;
            if (_read > declared || budget.Value < 0)
            {
                throw new InvalidEapException(TooLarge(zipName));
            }

            if (read == 0 && asked > 0 && _read != declared)
            {
                throw new InvalidEapException(Broken(zipName));
            }

            return read;
        }
    }

    /// <summary>Hashes what passes through.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            hash.AppendData(buffer, offset, read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>The entry's stream that also closes its archive.</summary>
    private sealed class OwningStream(Stream inner, ZipArchive zip) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                zip.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await zip.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>"3 files".</summary>
    internal static string Count(int files) =>
        string.Create(CultureInfo.InvariantCulture, $"{files} {(files == 1 ? "file" : "files")}");
}
