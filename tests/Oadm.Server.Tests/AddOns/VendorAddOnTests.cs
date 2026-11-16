using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Oadm.Contracts;
using Oadm.Server.AddOns;

namespace Oadm.Server.Tests.AddOns;

public sealed class VendorAddOnTests
{
    private const string Id = "sample.vendor";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ApprovedVendorPackageInstallsAndExportsOnlySignedClientFiles()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Package(key);
        using var rig = Setup(zip, key);
        var result = await InstallAsync(rig, zip);
        Assert.Equal(AddOnState.Installed, result.State);
        Assert.Equal("1.0.0", result.Version);
        var files = await rig.Installer.GetClientPartAsync(Id, CancellationToken.None);
        Assert.Equal(new[]
        {
            "Sample.Client.dll", "Sample.Contracts.dll", VendorPluginManifest.FileName,
            VendorPluginManifest.SignatureFileName, "plugin.json",
        }, files.Files.Select(f => f.RelativePath).Order(StringComparer.Ordinal).ToArray());

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var rows = await rig.Installer.ListAsync(CancellationToken.None);
        Assert.Equal("1.0.0", Assert.Single(rows, row => row.Entry.Id == Id).Version);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("changed-file")]
    [InlineData("extra-file")]
    [InlineData("wrong-platform")]
    [InlineData("export-server")]
    public async Task SignedInventoryAndPublisherAreCheckedBeforeLoading(string failure)
    {
        using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Package(signer, failure);
        using var rig = Setup(zip, failure == "wrong-key" ? other : signer);
        await Assert.ThrowsAsync<AddOnException>(() => InstallAsync(rig, zip));
        Assert.DoesNotContain(rig.Loader.Packages, package => package.Manifest.Id == Id);
        Assert.False(Directory.Exists(Path.Combine(rig.Root, Id)));
    }

    [Fact]
    public async Task ChangedInstalledFileIsRefusedOnRestart()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Package(key);
        using var rig = Setup(zip, key);
        await InstallAsync(rig, zip);
        File.AppendAllText(Path.Combine(rig.Root, Id, "Sample.Server.dll"), "changed");

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var rows = await rig.Installer.ListAsync(CancellationToken.None);
        Assert.Equal(AddOnState.Failed, Assert.Single(rows, row => row.Entry.Id == Id).State);
        Assert.DoesNotContain(rig.Loader.Packages, package => package.Manifest.Id == Id);
    }

    [Theory]
    [InlineData(false, "1.1.0")]
    [InlineData(true, "1.0.0")]
    public async Task VendorUpdateStartsOnRestartAndLoadFailureRestoresLastGood(bool broken, string expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var zip = Package(key);
        using var rig = Setup(zip, key);
        await InstallAsync(rig, zip);
        var update = Package(key, broken ? "broken-server" : null, "1.1.0");
        rig.Limits = options => options with { VendorAddOns = [Approved(update, key, "1.1.0")] };
        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var waiting = await InstallAsync(rig, update);
        Assert.True(waiting.UpdateWaits);
        Assert.Equal("1.0.0", waiting.Version);

        rig.Restart();
        await rig.Installer.LoadInstalledAsync(CancellationToken.None);
        var rows = await rig.Installer.ListAsync(CancellationToken.None);
        var installed = Assert.Single(rows, row => row.Entry.Id == Id);
        Assert.Equal((AddOnState.Installed, expected), (installed.State, installed.Version));
    }

    private static Task<AddOnStatus> InstallAsync(AddOnRig rig, byte[] zip) =>
        rig.Installer.InstallFileAsync(new AddOnUpload(Id, "sample.zip", zip.Length, AddOnRig.ChunksOf(zip)),
            new ProgressLog(), CancellationToken.None);

    private static AddOnRig Setup(byte[] zip, ECDsa key)
    {
        var rig = new AddOnRig
        {
            Limits = options => options with
            {
                VendorAddOns = [Approved(zip, key, "1.0.0")],
            },
        };
        rig.Restart();
        return rig;
    }

    private static VendorAddOn Approved(byte[] zip, ECDsa key, string version) => new(Id, "Sample", "Test plugin.", version,
        VendorPluginManifest.Sha256(zip), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

    private static byte[] Package(ECDsa key, string? failure = null, string version = "1.0.0")
    {
        var assembly = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Oadm.Plugins.Restart.Server.dll"));
        var payload = new Dictionary<string, (byte[] Bytes, bool Client)>(StringComparer.Ordinal)
        {
            ["plugin.json"] = (Encoding.UTF8.GetBytes($$"""{"id":"sample.vendor","version":"{{version}}","minSdkVersion":"0.10.0","displayName":"Sample","enabledByDefault":false}"""), true),
            ["Sample.Server.dll"] = (failure == "broken-server" ? [0, 1, 2] : assembly, false),
            ["Sample.Client.dll"] = (assembly, true),
            ["Sample.Contracts.dll"] = (assembly, true),
        };
        if (failure == "export-server") payload["Another.Server.dll"] = (assembly, true);
        var manifest = new VendorPluginManifest(1, Id, version, AddOnRig.Version,
            failure == "wrong-platform" ? "win-x64" : AddOnRig.Runtime,
            VendorPluginManifest.Sha256(key.ExportSubjectPublicKeyInfo()),
            payload.Select(p => new VendorPluginFile(p.Key, p.Value.Bytes.Length, VendorPluginManifest.Sha256(p.Value.Bytes), p.Value.Client)).ToArray());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
        var signature = key.SignData(VendorPluginManifest.SigningInput(bytes), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (failure == "changed-file") payload["Sample.Contracts.dll"] = ([1, 2, 3], true);
        if (failure == "extra-file") payload["Unexpected.pdb"] = ([1, 2, 3], false);

        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in payload) Write(archive, file.Key, file.Value.Bytes);
            Write(archive, VendorPluginManifest.FileName, bytes);
            Write(archive, VendorPluginManifest.SignatureFileName, signature);
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive archive, string path, byte[] data)
    {
        using var file = archive.CreateEntry(path).Open();
        file.Write(data);
    }
}
