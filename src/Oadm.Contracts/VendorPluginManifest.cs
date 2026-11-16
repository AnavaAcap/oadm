using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Contracts;

/// <summary>The signed inventory of one vendor plugin package. The publisher key comes from the host's trust policy.</summary>
public sealed record VendorPluginManifest(
    int SchemaVersion, string PluginId, string Version, string HostVersion, string RuntimeId,
    string PublisherKeyId, IReadOnlyList<VendorPluginFile> Files)
{
    public const string FileName = "oadm-vendor-manifest.json";
    public const string SignatureFileName = "oadm-vendor-manifest.sig";
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxSignatureBytes = 80;
    public const int MaxFiles = 256;

    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("OADM vendor plugin manifest v1\0");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    /// <summary>Verify the manifest bytes before accepting their claims. Signatures are ECDSA P-256/SHA-256, DER encoded.</summary>
    public static VendorPluginManifest Verify(
        ReadOnlyMemory<byte> bytes, ReadOnlyMemory<byte> signature, ReadOnlyMemory<byte> trustedPublisherKey,
        string pluginId, string hostVersion, string runtimeId)
    {
        if (bytes.Length is < 1 or > MaxManifestBytes || signature.Length is < 8 or > MaxSignatureBytes
            || trustedPublisherKey.Length != 91)
        {
            throw new InvalidDataException("The vendor plugin signature or manifest has an invalid size.");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(trustedPublisherKey.Span, out var used);
            if (used != trustedPublisherKey.Length ||
                key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
                !key.VerifyData(SigningInput(bytes), signature.Span, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new InvalidDataException("The vendor plugin signature does not match the trusted publisher.");
            }

            var manifest = JsonSerializer.Deserialize<VendorPluginManifest>(bytes.Span, Json)
                ?? throw new InvalidDataException("The vendor plugin manifest is empty.");
            manifest.Validate(pluginId, hostVersion, runtimeId, Sha256(trustedPublisherKey.Span));
            return manifest;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException("The vendor plugin manifest or signature could not be read.", ex);
        }
    }

    /// <summary>Construct the input signed by a vendor's private release key.</summary>
    public static byte[] SigningInput(ReadOnlyMemory<byte> bytes)
    {
        var input = new byte[Purpose.Length + bytes.Length];
        Purpose.AsSpan().CopyTo(input);
        bytes.Span.CopyTo(input.AsSpan(Purpose.Length));
        return input;
    }

    public static string Sha256(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private void Validate(string pluginId, string hostVersion, string runtimeId, string keyId)
    {
        if (SchemaVersion != 1 || PluginId != pluginId || !SafePluginId(PluginId) || HostVersion != hostVersion
            || RuntimeId != runtimeId || PublisherKeyId != keyId || Version is null || Version.Length > 32
            || !System.Version.TryParse(Version, out var version) || version.Build < 0 || version.Revision >= 0
            || version.ToString() != Version || Files is null || Files.Count is < 2 or > MaxFiles)
        {
            throw new InvalidDataException("The signed vendor package is incompatible with this plugin, host, or platform.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { FileName, SignatureFileName };
        foreach (var file in Files)
        {
            if (file is null || !SafePath(file.Path) || !names.Add(file.Path)
                || file.Size is < 1 or > 128 * 1024 * 1024 || !IsDigest(file.Sha256)
                || file.Client && !AllowedClientFile(file.Path))
            {
                throw new InvalidDataException("The vendor package declares an invalid or duplicate file.");
            }
        }

        if (!Files.Any(f => f.Path == "plugin.json" && f.Client) ||
            !Files.Any(f => !f.Client && !f.Path.Contains('/', StringComparison.Ordinal) &&
                f.Path.EndsWith(".Server.dll", StringComparison.Ordinal)) ||
            Files.Any(f => f.Client && f.Path.Contains('/', StringComparison.Ordinal) &&
                f.Path.EndsWith(".Client.dll", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The vendor package has no valid plugin manifest or assembly layout.");
        }
    }

    public static bool SafePluginId(string? id) =>
        id is { Length: > 0 and <= 80 } && id.Split('.').All(p => p.Length > 0 &&
            p.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'));

    /// <summary>Refuse extra files and links in a staged package before client or server loading.</summary>
    public static void VerifyInventory(string folder, IEnumerable<VendorPluginFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        expected.Add(FileName);
        expected.Add(SignatureFileName);
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The vendor plugin contains a linked directory.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The vendor plugin contains a linked file.");
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(path);
                else if (!expected.Remove(Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/')))
                    throw new InvalidDataException("The vendor plugin contains an unexpected file.");
            }
        }

        if (expected.Count != 0) throw new InvalidDataException("The vendor plugin is missing a signed file.");
    }

    private static bool IsDigest(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool AllowedClientFile(string path) =>
        path == "plugin.json" ||
        !path.EndsWith(".Server.dll", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".dll", StringComparison.Ordinal) || path.EndsWith(".deps.json", StringComparison.Ordinal));

    private static bool SafePath(string? path) =>
        path is { Length: > 0 and <= 240 } && path.Split('/').All(ValidSegment);

    private static bool ValidSegment(string name)
    {
        if (name is not { Length: > 0 and <= 100 } || !char.IsAsciiLetterOrDigit(name[0]) ||
            name.EndsWith('.') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
        {
            return false;
        }

        var stem = name.Split('.', 2)[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && char.IsAsciiDigit(stem[3]) &&
                (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)));
    }
}

/// <summary>A package-relative file and its signed size and SHA-256. Client files form an explicit allowlist.</summary>
public sealed record VendorPluginFile(string Path, long Size, string Sha256, bool Client);
