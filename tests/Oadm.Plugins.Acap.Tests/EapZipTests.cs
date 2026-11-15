using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>The shared rules of a vendor's ZIP on the server (<see cref="EapZip.ReadAsync"/>) and in the client (<see cref="EapPackageFiles"/>).</summary>
public sealed class EapZipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-eapzip-" + Guid.NewGuid().ToString("N"));

    public EapZipTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Eap(string arch = "aarch64") => EapBuilder.FromManifest(EapBuilder.Manifest(architecture: arch));

    private static byte[] Zip(CompressionLevel level, params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name, level).Open();
                stream.Write(content);
            }
        }

        return output.ToArray();
    }

    private static async Task<string> RefusedOnServer(byte[] zip)
    {
        using var stream = new MemoryStream(zip, writable: false);
        return (await Assert.ThrowsAsync<InvalidEapException>(() => EapZip.ReadAsync(stream, "vendor.zip", CancellationToken.None))).Message;
    }

    private async Task<string> RefusedInClient(byte[] zip)
    {
        var path = Path.Combine(_dir, "vendor.zip");
        await File.WriteAllBytesAsync(path, zip);
        using var files = new EapPackageFiles { TempRoot = _dir };
        return (await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([path], null, CancellationToken.None))).Message;
    }

    [Fact]
    public async Task The_server_reads_every_build_with_its_hash_and_the_mapping_file_next_to_them()
    {
        var aarch64 = Eap();
        var zip = EapBuilder.Zip(("lpr/a.eap", aarch64), ("lpr/b.eap", Eap("armv7hf")), ("lpr/oadm-packages.json", Encoding.UTF8.GetBytes("""{"unmatched":"skip","packages":[{"file":"b.eap","soc":["A8"]}]}""")),
            ("__MACOSX/lpr/._a.eap", [1]), ("__MACOSX/lpr/oadm-packages.json", [1]));
        using var stream = new MemoryStream(zip, writable: false);

        var contents = await EapZip.ReadAsync(stream, "vendor.zip", CancellationToken.None);

        Assert.Equal(["a.eap", "b.eap"], contents.Builds.Select(b => b.Name));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(aarch64)), contents.Builds[0].Sha256);
        Assert.Equal(aarch64.Length, contents.Builds[0].Size);
        Assert.Equal("b.eap", Assert.Single(contents.Rules).File);
        Assert.Equal(AcapUnmatched.Skip, contents.Unmatched);
    }

    [Theory]
    [InlineData("anava/oadm-packages.json", "anava/builds/", "vendor.zip has oadm-packages.json in the folder anava, which holds no .eap file. Put it at the top level or next to the .eap files.")]
    [InlineData("docs/oadm-packages.json", "", "vendor.zip has oadm-packages.json in the folder docs, which holds no .eap file. Put it at the top level or next to the .eap files.")]
    public async Task A_mapping_file_where_it_does_not_count_is_refused_not_ignored(string mapping, string folder, string expected)
    {
        // Ignoring it would give every device the automatic pick that the vendor's rows may keep out.
        var zip = EapBuilder.Zip((folder + "a.eap", Eap()), (folder + "b.eap", Eap("armv7hf")), (mapping, """{"unmatched":"skip","packages":[]}"""u8.ToArray()));

        Assert.Equal(expected, await RefusedOnServer(zip));
        Assert.Equal(expected, await RefusedInClient(zip));
    }

    [Theory]
    [InlineData("../evil.eap")]
    [InlineData("lpr/../../evil.eap")]
    [InlineData("/etc/evil.eap")]
    [InlineData("C:/evil.eap")]
    public async Task A_name_with_a_path_outside_the_ZIP_is_refused(string name)
    {
        var zip = EapBuilder.Zip(("a.eap", Eap()), (name, Eap()));

        Assert.Equal("vendor.zip has a file with a path outside the ZIP.", await RefusedOnServer(zip));
        Assert.Equal("vendor.zip has a file with a path outside the ZIP.", await RefusedInClient(zip));
    }

    [Fact]
    public async Task An_entry_that_unpacks_to_far_more_than_its_size_is_refused()
    {
        var zip = Zip(CompressionLevel.SmallestSize, ("bomb.eap", new byte[16 * 1024 * 1024]));

        Assert.Equal(EapZip.TooLarge("vendor.zip"), await RefusedOnServer(zip));
        Assert.Equal(EapZip.TooLarge("vendor.zip"), await RefusedInClient(zip));
    }

    [Fact]
    public async Task Too_many_entries_and_two_packages_of_one_name_are_refused()
    {
        var many = EapBuilder.Zip([.. Enumerable.Range(0, EapZip.MaxEntries + 1).Select(i => ($"f{i}.txt", Array.Empty<byte>())), ("a.eap", Eap())]);
        Assert.Equal("vendor.zip has too many files.", await RefusedOnServer(many));

        var twice = EapBuilder.Zip(("aarch64/app.eap", Eap()), ("armv7hf/APP.eap", Eap("armv7hf")));
        Assert.Equal("vendor.zip has two files named APP.eap.", await RefusedOnServer(twice));
        Assert.Equal("vendor.zip has two files named APP.eap.", await RefusedInClient(twice));
    }

    [Fact]
    public async Task Two_mapping_files_and_a_broken_one_are_refused()
    {
        var two = EapBuilder.Zip(("a.eap", Eap()), ("oadm-packages.json", "{}"u8.ToArray()), ("sub/a2.eap", Eap()), ("sub/oadm-packages.json", "{}"u8.ToArray()));
        Assert.Equal("vendor.zip has more than one oadm-packages.json.", await RefusedOnServer(two));

        var broken = EapBuilder.Zip(("a.eap", Eap()), ("oadm-packages.json", "{\"packages\":[}"u8.ToArray()));
        Assert.Equal("oadm-packages.json is not valid JSON.", await RefusedOnServer(broken));
    }

    [Fact]
    public async Task The_client_keeps_the_names_of_the_ZIP_and_its_mapping_file()
    {
        var path = Path.Combine(_dir, "vendor.zip");
        await File.WriteAllBytesAsync(path, EapBuilder.Zip(("lpr/a:b.eap", Eap()), ("lpr/oadm-packages.json", """{"packages":[]}"""u8.ToArray())));
        using var files = new EapPackageFiles { TempRoot = _dir };

        await files.PickAsync([path], null, CancellationToken.None);

        Assert.Equal(["a:b.eap"], files.Names); // the name a mapping file uses; the file on disk is safe ("a_b.eap")
        Assert.Equal("a_b.eap", Path.GetFileName(files.Paths[0]));
        Assert.Equal("""{"packages":[]}""", Encoding.UTF8.GetString(files.RulesJson!));
    }
}
