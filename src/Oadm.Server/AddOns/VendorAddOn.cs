using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

using Oadm.Contracts;

namespace Oadm.Server.AddOns;

/// <summary>One package approved by the server administrator, separate from the package contents.</summary>
public sealed record VendorAddOn(
    string Id, string DisplayName, string Description, string Version, string ArchiveSha256,
    string PublisherPublicKey)
{
    public byte[] PublisherKey()
    {
        var key = Convert.FromBase64String(PublisherPublicKey);
        if (key.Length != 91)
        {
            throw new InvalidOperationException("Configure an ECDSA P-256 public key in SubjectPublicKeyInfo form.");
        }

        return key;
    }

    internal AddOnEntry Entry()
    {
        if (!VendorPluginManifest.SafePluginId(Id) || Id.StartsWith("oadm.", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 100 || Description is null || Description.Length > 300 ||
            !System.Version.TryParse(Version, out var version) || version.Build < 0 || version.Revision >= 0 || version.ToString() != Version ||
            ArchiveSha256 is not { Length: 64 } || ArchiveSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new InvalidOperationException("The configured vendor plugin entry is invalid.");
        }

        _ = PublisherKey();
        return new AddOnEntry(Id, DisplayName, Description) { Vendor = this };
    }
}

internal static class VendorAddOnFiles
{
    public static VendorPluginManifest Verify(
        string folder, VendorAddOn vendor, string hostVersion, string runtimeId, bool requireApprovedVersion,
        string? expectedManifestSha256, out string manifestSha256)
    {
        var bytes = ReadBounded(folder, VendorPluginManifest.FileName, VendorPluginManifest.MaxManifestBytes);
        manifestSha256 = VendorPluginManifest.Sha256(bytes);
        if (expectedManifestSha256 is not null && manifestSha256 != expectedManifestSha256)
        {
            throw new InvalidDataException("The vendor manifest differs from the approved import.");
        }
        var signature = ReadBounded(folder, VendorPluginManifest.SignatureFileName, VendorPluginManifest.MaxSignatureBytes);
        var manifest = VendorPluginManifest.Verify(bytes, signature, vendor.PublisherKey(), vendor.Id, hostVersion, runtimeId);
        if (requireApprovedVersion && manifest.Version != vendor.Version)
        {
            throw new InvalidDataException("This vendor plugin version is not the one approved by the administrator.");
        }

        VendorPluginManifest.VerifyInventory(folder, manifest.Files);

        foreach (var file in manifest.Files)
        {
            using var stream = File.OpenRead(Path.Combine(folder, file.Path));
            if (stream.Length != file.Size ||
                !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A vendor package file does not match its signed inventory.");
            }

            if (file.Client && file.Path.EndsWith(".dll", StringComparison.Ordinal))
            {
                stream.Position = 0;
                using var assembly = new PEReader(stream);
                if (!assembly.HasMetadata || assembly.PEHeaders.CorHeader is not { Flags: var flags } || (flags & CorFlags.ILOnly) == 0)
                {
                    throw new InvalidDataException("Vendor client components must be managed assemblies.");
                }
            }
        }

        return manifest;
    }

    public static IReadOnlyList<AddOnClientFile> ClientPart(
        string folder, VendorPluginManifest manifest, CancellationToken ct)
    {
        var files = new List<AddOnClientFile>();
        if (!manifest.Files.Any(file => file.Client && file.Path.EndsWith(".Client.dll", StringComparison.Ordinal)))
        {
            return files;
        }

        long total = 0;
        foreach (var file in manifest.Files.Where(file => file.Client))
        {
            total += file.Size;
            if (total > AddOnFolders.MaxClientPartBytes)
            {
                throw new InvalidDataException("The vendor plugin client component is too large.");
            }

            var bytes = ReadBounded(folder, file.Path, (int)file.Size);
            if (bytes.Length != file.Size || VendorPluginManifest.Sha256(bytes) != file.Sha256)
            {
                throw new InvalidDataException("A vendor client file no longer matches its signed inventory.");
            }

            files.Add(new AddOnClientFile(file.Path, bytes, file.Sha256));
            ct.ThrowIfCancellationRequested();
        }

        foreach (var metadata in new[] { (VendorPluginManifest.FileName, VendorPluginManifest.MaxManifestBytes),
                                          (VendorPluginManifest.SignatureFileName, VendorPluginManifest.MaxSignatureBytes) })
        {
            var bytes = ReadBounded(folder, metadata.Item1, metadata.Item2);
            files.Add(new AddOnClientFile(metadata.Item1, bytes, VendorPluginManifest.Sha256(bytes)));
        }

        return files;
    }

    private static byte[] ReadBounded(string folder, string relativePath, int maxBytes)
    {
        using var stream = File.OpenRead(Path.Combine(folder, relativePath));
        if (stream.Length is < 1 || stream.Length > maxBytes)
        {
            throw new InvalidDataException("A vendor package file has an invalid size.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("A vendor package file changed while it was read.");
        }

        return bytes;
    }
}
