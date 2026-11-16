using Oadm.Contracts;
using Oadm.Contracts.V1;

namespace Oadm.Client.Plugins.AddOns;

internal static class VendorClientPart
{
    public static void Verify(string folder, AddOnInfo addOn)
    {
        var bytes = Read(folder, VendorPluginManifest.FileName, VendorPluginManifest.MaxManifestBytes);
        var signature = Read(folder, VendorPluginManifest.SignatureFileName, VendorPluginManifest.MaxSignatureBytes);
        var manifest = VendorPluginManifest.Verify(bytes, signature, addOn.VendorPublisherKey.Memory,
            addOn.Id, addOn.VendorHostVersion, addOn.VendorRuntimeId);
        if (manifest.Version != addOn.Version)
        {
            throw new InvalidDataException("The client component version differs from its signed manifest.");
        }

        var expected = manifest.Files.Where(file => file.Client).ToArray();
        VendorPluginManifest.VerifyInventory(folder, expected);

        foreach (var file in expected)
        {
            var data = Read(folder, file.Path, file.Size);
            if (data.Length != file.Size || VendorPluginManifest.Sha256(data) != file.Sha256)
            {
                throw new InvalidDataException("A client component file does not match its signed inventory.");
            }
        }
    }

    private static byte[] Read(string folder, string file, long maxBytes)
    {
        using var stream = File.OpenRead(Path.Combine(folder, file));
        if (stream.Length is < 1 || stream.Length > maxBytes)
        {
            throw new InvalidDataException("A client component file has an invalid size.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("A client component file changed while it was read.");
        }

        return bytes;
    }
}
