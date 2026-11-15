using System.Text;
using System.Text.Json;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// Several builds of one application (per architecture and AXIS OS, also of other versions): the pick per device group, the
/// choice of a person or an agent, the vendor's mapping file, the task's run with a fresh read and the plan of agents.
/// </summary>
public sealed class PackagesPerArchitectureTests : IDisposable
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private static readonly IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi> AppApi = [new("application", "1.0")];

    private readonly string _dir =Path.Combine(Path.GetTempPath(), "oadm-acap-multi-" + Guid.NewGuid().ToString("N"));

    public PackagesPerArchitectureTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static EapManifest Manifest(string? arch, string app = "hello", string version = "1.2.0", string? min = null, string? max = null, string schema = "1.7.1") =>
        EapReader.Parse(EapBuilder.Manifest(appName: app, version: version, architecture: arch, osMin: min, osMax: max, schema: schema), null);

    private static byte[] Eap(string? arch, string version = "1.2.0", string? min = null, string? max = null, string schema = "1.7.1", string app = "hello") =>
        EapBuilder.FromManifest(EapBuilder.Manifest(appName: app, version: version, architecture: arch, osMin: min, osMax: max, schema: schema));

    private static List<AcapBuild> Builds(params (string Name, EapManifest Package)[] packages) => [.. packages.Select(p => new AcapBuild(p.Name, p.Name, p.Package))];

    private static AcapDeviceGroup Group(string fw, string arch = "aarch64", string model = "P3265-V", string? soc = "Axis Artpec-8") => new(model, fw, arch, soc);

    private string WriteEap(string name, string? arch, string app = "hello", string version = "1.2.0", string? min = null, string? max = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Eap(arch, version, min, max, app: app));
        return path;
    }

    private static Task Run(RecordingContext ctx, string? payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    private static (FakeAcapDevice Device, RecordingContext Ctx, string Payload) TwoPackages(string deviceArch, string second = "armv7hf")
    {
        var device = new FakeAcapDevice { Architecture = deviceArch };
        var files = new InMemoryUploadedFiles();
        var arm64 = files.Add("hello_aarch64.eap", Eap("aarch64"));
        var armv7 = files.Add("hello_" + second + ".eap", Eap(second));
        var payload = new AcapPayload
        {
            Action = AcapAction.Install,
            Application = "hello",
            FileId = arm64.Id,
            Sha256 = arm64.Sha256,
            Files = [new(arm64.Id, arm64.Sha256, "aarch64"), new(armv7.Id, armv7.Sha256, second)],
        }.ToJson();
        return (device, new RecordingContext(device, files), payload);
    }

    // ---- The pick per device group (the same pure function for the dialog, the plan and the task) ----

    [Fact]
    public void Two_aarch64_builds_are_chosen_by_their_AXIS_OS_range()
    {
        var builds = Builds(("lts.eap", Manifest("aarch64", min: "10.0", max: "10.12")), ("new.eap", Manifest("aarch64", min: "11.0", max: "12")));

        Assert.Equal(0, AcapBuildChooser.Choose(builds, Group("10.12.5")).Index);
        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group("12.11.77")).Index);
        var none = AcapBuildChooser.Choose(builds, Group("9.80.3"));
        Assert.Equal(AcapPick.None, none.Pick);
        Assert.Equal("No package for AXIS OS 9.80.3.", none.Reason);
        Assert.Equal("No package for armv7hf.", AcapBuildChooser.Choose(builds, Group("12.11.77", arch: "armv7hf")).Reason);
    }

    [Fact]
    public void Each_track_gets_its_own_version_and_the_newest_fitting_wins()
    {
        var builds = Builds(("lpr_2.4.eap", Manifest("aarch64", version: "2.4.0", min: "10.0", max: "10.12")), ("lpr_2.6.eap", Manifest("aarch64", version: "2.6.1", min: "11.0")),
            ("lpr_2.5.eap", Manifest("aarch64", version: "2.5.0", min: "11.0")));

        Assert.Equal(0, AcapBuildChooser.Choose(builds, Group("10.12.1")).Index);
        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group("11.11.5")).Index); // 2.6.1 over 2.5.0
        Assert.Equal("2.6.1, 2.5.0 and 2.4.0", AcapCompatibility.VersionsText(builds.Select(b => b.Package)));
    }

    [Fact]
    public void Of_equal_versions_the_narrower_range_wins_and_a_tie_is_unclear()
    {
        var narrower = Builds(("wide.eap", Manifest("aarch64", min: "11.0", max: "12")), ("lts.eap", Manifest("aarch64", min: "11.11", max: "11.11")));
        Assert.Equal(1, AcapBuildChooser.Choose(narrower, Group("11.11.5")).Index);

        var same = Builds(("a.eap", Manifest("aarch64", min: "11.0", max: "12")), ("b.eap", Manifest("aarch64", min: "11.0", max: "12")));
        var tie = AcapBuildChooser.Choose(same, Group("12.11.77"));
        Assert.Equal(AcapPick.Unclear, tie.Pick);
        Assert.Equal([0, 1], tie.Candidates);
        Assert.Equal("Several packages fit: a.eap, b.eap.", tie.Reason);

        // A build without a range competing with another is never decided by OADM.
        var noRange = Builds(("a.eap", Manifest("aarch64")), ("b.eap", Manifest("aarch64", min: "12.0")));
        Assert.True(AcapBuildChooser.Choose(noRange, Group("12.11.77")).IsUnclear);
    }

    [Fact]
    public void A_package_for_any_architecture_is_the_last_resort()
    {
        var builds = Builds(("native.eap", Manifest("aarch64")), ("any.eap", Manifest(null, schema: "1.3")));

        Assert.Equal(0, AcapBuildChooser.Choose(builds, Group("12.11.77")).Index); // the native one fits
        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group("12.11.77", arch: "armv7hf")).Index); // none native for armv7hf
        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group("11.0.4")).Index); // the native one needs AXIS OS 12.0
        Assert.Null(AcapCompatibility.CheckPackageSet([("native.eap", builds[0].Package), ("any.eap", builds[1].Package)]));
    }

    [Fact]
    public void A_choice_decides_an_unclear_group_but_never_one_that_does_not_fit()
    {
        var builds = Builds(("a.eap", Manifest("aarch64")), ("b.eap", Manifest("aarch64")), ("v7.eap", Manifest("armv7hf")));
        var group = Group("12.11.77");

        var chosen = AcapBuildChooser.Choose(builds, group, mapping: [AcapPackageMapping.For(group, "b.eap")]);
        Assert.Equal((1, AcapBuildChoice.ChosenByPerson), (chosen.Index, chosen.DecidedBy));

        var misfit = AcapBuildChooser.Choose(builds, group, mapping: [AcapPackageMapping.For(group, "v7.eap")]);
        Assert.Equal(AcapPick.None, misfit.Pick);
        Assert.Equal("v7.eap does not fit: it is for armv7hf.", misfit.Reason);

        // A choice for another AXIS OS does not apply: the group is unclear again.
        Assert.True(AcapBuildChooser.Choose(builds, Group("12.11.80"), mapping: [AcapPackageMapping.For(group, "b.eap")]).IsUnclear);

        // Without a model the choice is for every model of that AXIS OS and architecture.
        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group("12.11.77", model: "M3086-V"), mapping: [new AcapPackageMapping("12.11.77", "arm64", "b.eap")]).Index);
    }

    [Fact]
    public void Sets_of_builds_are_one_application_with_unique_file_names()
    {
        Assert.Null(AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("aarch64", version: "1.3.0")), ("c.eap", Manifest("all"))]));
        Assert.Equal("b.eap is another application (Hello World). Choose the files of one application.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("b.eap", Manifest("armv7hf", app: "other"))]));
        Assert.Equal("Two files are named A.eap. Rename one of them.",
            AcapCompatibility.CheckPackageSet([("a.eap", Manifest("aarch64")), ("A.eap", Manifest("armv7hf"))]));
        Assert.Equal(0, AcapBuildChooser.Choose(Builds(("a.eap", Manifest("aarch64"))), Group("9.80", arch: "mips")).Index); // one package: its checks decide
    }

    // ---- The vendor's mapping file ----

    private const string Mapping = """
        {
          "packages": [
            { "file": "lpr_lts.eap", "axisOs": { "min": "10.0", "max": "10.12" } },
            { "file": "lpr_cv25.eap", "soc": ["CV25"] },
            { "file": "lpr_q1656.eap", "models": ["AXIS Q1656-LE"] },
            { "file": "lpr_a8.eap", "soc": ["ARTPEC-8"], "architecture": "aarch64" }
          ]
        }
        """;

    private static List<AcapBuild> MappedBuilds() => Builds(
        ("lpr_lts.eap", Manifest("aarch64", version: "2.4.0", min: "10.0", max: "10.12")),
        ("lpr_cv25.eap", Manifest("aarch64", version: "2.6.0", min: "11.0")),
        ("lpr_q1656.eap", Manifest("aarch64", version: "2.6.0", min: "11.0")),
        ("lpr_a8.eap", Manifest("aarch64", version: "2.6.0", min: "11.0")));

    private static IReadOnlyList<AcapPackageRule> Rules(string json = Mapping) =>
        AcapPackageRules.Parse(Encoding.UTF8.GetBytes(json), ["lpr_lts.eap", "LPR_CV25.eap", "lpr_q1656.eap", "lpr_a8.eap"]);

    [Fact]
    public void Mapping_rows_match_by_model_SoC_and_range_and_the_most_specific_wins()
    {
        var builds = MappedBuilds();
        var rules = Rules();

        Assert.Equal(4, rules.Count);
        var bySoc = AcapBuildChooser.Choose(builds, Group("11.11.5", model: "M3086-V", soc: "Ambarella CV25"), rules);
        Assert.Equal((1, "Row 2 of the mapping file"), (bySoc.Index, bySoc.DecidedBy));
        Assert.Equal(3, AcapBuildChooser.Choose(builds, Group("12.11.77", soc: "Axis Artpec-8"), rules).Index); // ARTPEC-8 = Axis Artpec-8
        Assert.Equal(2, AcapBuildChooser.Choose(builds, Group("12.11.77", model: "q1656-le", soc: "Axis Artpec-8"), rules).Index); // models before the SoC
        Assert.Equal(0, AcapBuildChooser.Choose(builds, Group("10.12.1", model: "M3086-V", soc: "Axis Artpec-6"), rules).Index);

        // No row matches: the automatic pick of the manifests.
        Assert.True(AcapBuildChooser.Choose(builds, Group("12.11.77", model: "M3086-V", soc: "Axis Artpec-7"), rules).IsUnclear);
    }

    [Fact]
    public void Two_equally_specific_rows_are_unclear_and_a_row_never_overrides_safety()
    {
        var builds = MappedBuilds();
        var tie = AcapBuildChooser.Choose(builds, Group("12.11.77"), Rules("""{"packages":[{"file":"lpr_cv25.eap","architecture":"aarch64"},{"file":"lpr_a8.eap","axisOs":{"min":"12"}}]}"""));
        Assert.Equal(AcapPick.Unclear, tie.Pick);
        Assert.Equal("Rows 1 and 2 of the mapping file fit equally: lpr_cv25.eap, lpr_a8.eap.", tie.Reason);

        // The LTS build for a device on AXIS OS 12: No package with the reason, never a wrong install.
        var misfit = AcapBuildChooser.Choose(builds, Group("12.11.77", model: "P1468-LE"), Rules("""{"packages":[{"file":"lpr_lts.eap","models":["P1468-LE"]}]}"""));
        Assert.Equal(AcapPick.None, misfit.Pick);
        Assert.Equal("lpr_lts.eap from row 1 of the mapping file does not fit: it needs AXIS OS 10.0 - 10.12.", misfit.Reason);
    }

    [Theory]
    [InlineData("""{"packages":[{"file":"gone.eap"}]}""", "oadm-packages.json row 1 names gone.eap, which is not among the .eap files.")]
    [InlineData("""{"packages":[{"models":["P3265-V"]}]}""", "oadm-packages.json row 1 has no \"file\".")]
    [InlineData("""{"packages":[{"file":"lpr_a8.eap","model":"P3265-V"}]}""", "oadm-packages.json row 1 has the unknown key \"model\".")]
    [InlineData("""{"packages":[{"file":"lpr_a8.eap"},]}""", "oadm-packages.json is not valid JSON.")]
    [InlineData("""{"packages":[{"file":"lpr_a8.eap"}] /* note */}""", "oadm-packages.json is not valid JSON.")]
    [InlineData("""{"rules":[]}""", "oadm-packages.json has the unknown key \"rules\".")]
    [InlineData("""{"packages":[{"file":"lpr_a8.eap","axisOs":{"min":"new"}}]}""", "oadm-packages.json row 1: \"axisOs.min\" must be a version such as 10.12.")]
    [InlineData("""{"packages":[{"file":"lpr_a8.eap","soc":"A8"}]}""", "oadm-packages.json row 1: \"soc\" must be a list of texts.")]
    public void A_mapping_file_that_is_not_right_is_refused_with_a_plain_reason(string json, string expected) =>
        Assert.Equal(expected, Assert.Throws<InvalidEapException>(() => Rules(json)).Message);

    [Fact]
    public void A_mapping_file_is_at_most_64_KB_and_500_rows()
    {
        var rows = string.Join(",", Enumerable.Repeat("""{"file":"lpr_a8.eap"}""", AcapPackageRules.MaxRows + 1));
        Assert.Equal("oadm-packages.json has more than 500 rows.", Assert.Throws<InvalidEapException>(() => Rules("{\"packages\":[" + rows + "]}")).Message);
        var big = "{\"packages\":[{\"file\":\"lpr_a8.eap\",\"models\":[\"" + new string('x', 200) + "\"]}]" + new string(' ', AcapPackageRules.MaxBytes) + "}";
        Assert.Equal("oadm-packages.json is larger than 64 KB.", Assert.Throws<InvalidEapException>(() => Rules(big)).Message);
    }

    // ---- The task: a fresh read of the device decides again ----

    [Theory]
    [InlineData("aarch64", "hello_aarch64.eap")]
    [InlineData("armv7hf", "hello_armv7hf.eap")]
    public async Task Each_device_gets_the_package_of_its_architecture(string arch, string expected)
    {
        var (device, ctx, payload) = TwoPackages(arch);

        await Run(ctx, payload);

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal(expected, device.UploadedFileName);
        Assert.Equal("hello 1.2.0, aarch64 and armv7hf", StepRun.Detail(ctx.Steps, "Read package"));
        Assert.Equal($"AXIS OS 12.11.77, {arch}: {expected}", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("Verify installation: Done", StepRun.Lines(ctx.Steps)[8]);
    }

    [Fact]
    public async Task A_device_without_a_fitting_package_is_skipped_with_a_warning()
    {
        var (device, ctx, payload) = TwoPackages("armv7hf", second: "x86_64");

        await Run(ctx, payload);

        Assert.Empty(device.Writes);
        Assert.Equal(
            ["Check compatibility: Done", "Read package: Done", "Read SDK version: Done", "Read device info: Warning", "Read unsigned application setting: Skipped",
             "Read installed applications: Skipped", "Check package: Skipped", "Upload package: Skipped", "Verify installation: Skipped", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("No package for armv7hf. Nothing was changed.", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Equal("No package for armv7hf.", StepRun.Detail(ctx.Steps, "Upload package"));
        Assert.Contains("No package for armv7hf. Nothing was changed.", ctx.Warnings);
    }

    private static (InMemoryUploadedFiles Files, UploadedFile A, UploadedFile B) TwoEqualBuilds()
    {
        var files = new InMemoryUploadedFiles();
        return (files, files.Add("hello_a.eap", Eap("aarch64")), files.Add("hello_b.eap", Eap("aarch64")));
    }

    [Fact]
    public async Task The_task_honours_the_choice_of_an_unclear_group_and_refuses_without_one()
    {
        var (files, a, b) = TwoEqualBuilds();
        var device = new FakeAcapDevice();
        var ctx = new RecordingContext(device, files);
        var group = new AcapDeviceGroup("P3265-V", "12.11.77", "aarch64");
        var payload = new AcapPayload { Action = AcapAction.Install, Files = [new(a.Id, a.Sha256, "aarch64"), new(b.Id, b.Sha256, "aarch64")], Mapping = [AcapPackageMapping.For(group, b.Id)] };

        await Run(ctx, payload.ToJson());
        Assert.Equal("hello_b.eap", device.UploadedFileName);

        var unclear = new FakeAcapDevice();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RecordingContext(unclear, files), (payload with { Mapping = null }).ToJson()));
        Assert.Equal("Several packages fit: hello_a.eap, hello_b.eap. Choose the package for P3265-V, AXIS OS 12.11.77, aarch64. Nothing was changed.", refused.Message);
        Assert.Empty(unclear.Writes);
    }

    [Fact]
    public async Task A_chosen_file_that_does_not_fit_the_fresh_read_is_refused()
    {
        var files = new InMemoryUploadedFiles();
        var a = files.Add("hello_a.eap", Eap("aarch64"));
        var v7 = files.Add("hello_v7.eap", Eap("armv7hf"));
        var device = new FakeAcapDevice();
        var payload = new AcapPayload { Action = AcapAction.Install, Files = [new(a.Id, a.Sha256), new(v7.Id, v7.Sha256)], Mapping = [new AcapPackageMapping("12.11.77", "aarch64", v7.Id)] };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new RecordingContext(device, files), payload.ToJson()));

        Assert.Equal("hello_v7.eap does not fit: it is for armv7hf. Nothing was changed.", refused.Message);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task The_fresh_read_of_the_device_changes_the_choice()
    {
        var files = new InMemoryUploadedFiles();
        var lts = files.Add("hello_lts.eap", Eap("aarch64", version: "1.1.0", min: "10.0", max: "10.12"));
        var cur = files.Add("hello_12.eap", Eap("aarch64", version: "1.2.0", min: "11.0"));
        var payload = new AcapPayload { Action = AcapAction.Install, Files = [new(lts.Id, lts.Sha256), new(cur.Id, cur.Sha256)] }.ToJson();

        var old = new FakeAcapDevice { Firmware = "10.12.4" };
        await Run(new RecordingContext(old, files), payload);
        var upgraded = new FakeAcapDevice { Firmware = "12.11.77" }; // the cache said 10.12 when the dialog checked it
        await Run(new RecordingContext(upgraded, files), payload);

        Assert.Equal(("hello_lts.eap", "hello_12.eap"), (old.UploadedFileName, upgraded.UploadedFileName));
        Assert.Null(AcapPayload.Parse(payload).Version); // builds of two versions: the task name names none
    }

    [Fact]
    public async Task The_old_single_file_payload_still_installs()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var file = files.Add("hello.eap", Eap("aarch64"));
        var ctx = new RecordingContext(device, files);

        await Run(ctx, $$"""{"action":"install","fileId":"{{file.Id}}","sha256":"{{file.Sha256}}","application":"hello","version":"1.2.0"}""");

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal("AXIS OS 12.11.77, aarch64", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Single(AcapPayload.Parse($$"""{"action":"install","fileId":"{{file.Id}}"}""").AllFiles());
    }

    [Fact]
    public async Task A_file_that_is_not_the_checked_architecture_fails_before_any_write()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var a = files.Add("a.eap", Eap("aarch64"));
        var b = files.Add("b.eap", Eap("aarch64"));
        var ctx = new RecordingContext(device, files);
        var payload = new AcapPayload { Action = AcapAction.Install, FileId = a.Id, Files = [new(a.Id, a.Sha256, "aarch64"), new(b.Id, b.Sha256, "armv7hf")] }.ToJson();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, payload));

        Assert.Equal("b.eap is not the armv7hf package the dialog checked. Nothing was changed.", ex.Message);
        Assert.Empty(device.Writes);
    }

    [Theory]
    [InlineData("""{"action":"install","files":[]}""", "files")]
    [InlineData("""{"action":"install","files":[{"fileId":""}]}""", "files")]
    [InlineData("""{"action":"install","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"a","architecture":"armv7hf"}]}""", "files")]
    [InlineData("""{"action":"install","files":[{"fileId":"a"}],"zipFileId":"z"}""", "zipFileId")]
    [InlineData("""{"action":"install","zipFileId":"z","rules":[{"file":"a.eap"}]}""", "rules")]
    [InlineData("""{"action":"install","files":[{"fileId":"a"},{"fileId":"b"}],"rules":[{"file":"c"}]}""", "rules")]
    [InlineData("""{"action":"install","files":[{"fileId":"a"},{"fileId":"b"}],"mapping":[{"axisOs":"12.11.77","architecture":"aarch64","file":"c"}]}""", "mapping")]
    [InlineData("""{"action":"install","zipFileId":"z","mapping":[{"axisOs":"","architecture":"aarch64","file":"a.eap"}]}""", "mapping")]
    public void Invalid_package_lists_are_refused_at_their_field(string json, string field)
    {
        Assert.Equal(field, Assert.Throws<AcapPayloadException>(() => AcapPayload.Parse(json)).Field);
        Assert.Equal(field, Assert.Single(Plugin.ValidatePayload(json, Oadm.Sdk.Testing.FakeCallContext.AllDevices(new Oadm.Sdk.Testing.FakeCoreContext()))).Field);
    }

    [Fact]
    public void Several_builds_per_architecture_and_a_ZIP_are_valid_payloads()
    {
        var payload = AcapPayload.Parse("""{"action":"install","fileId":"a","files":[{"fileId":"a","architecture":"aarch64"},{"fileId":"b","architecture":"arm64"},{"fileId":"c"}],"mapping":[{"axisOs":"12.11.77","architecture":"aarch64","file":"b"}]}""");
        Assert.Equal(["a", "b", "c"], payload.AllFiles().Select(f => f.FileId));
        Assert.Equal([AcapPermissions.Install], Plugin.RequiredPermissions(payload.ToJson()));
        Assert.Equal("z", AcapPayload.Parse("""{"action":"install","zipFileId":"z"}""").ZipFileId);
    }

    // ---- A vendor's ZIP on the server (agents, the Assistant) ----

    internal static byte[] VendorZip(bool withMapping = true)
    {
        var entries = new List<(string, byte[])>
        {
            ("lpr/lpr_2.4_aarch64.eap", Eap("aarch64", version: "2.4.0", min: "10.0", max: "10.12", app: "lpr")),
            ("lpr/lpr_2.6_aarch64.eap", Eap("aarch64", version: "2.6.0", min: "11.0", app: "lpr")),
            ("lpr/lpr_2.6_armv7hf.eap", Eap("armv7hf", version: "2.6.0", min: "11.0", app: "lpr")),
            ("lpr/readme.txt", [1, 2, 3]),
        };
        if (withMapping)
        {
            entries.Add(("lpr/OADM-packages.json", Encoding.UTF8.GetBytes("""{"packages":[{"file":"lpr_2.6_aarch64.eap","models":["P3265-V"]},{"file":"lpr_2.4_aarch64.eap","axisOs":{"max":"10.12"}}]}""")));
        }

        return EapBuilder.Zip([.. entries]);
    }

    [Fact]
    public async Task The_task_installs_the_build_of_an_uploaded_ZIP_streamed_from_the_ZIP()
    {
        var files = new InMemoryUploadedFiles();
        var zip = files.Add("lpr.zip", VendorZip());
        var payload = new AcapPayload { Action = AcapAction.Install, ZipFileId = zip.Id }.ToJson();
        var device = new FakeAcapDevice { Firmware = "12.11.77" };
        var ctx = new RecordingContext(device, files);

        await Run(ctx, payload);

        Assert.Equal("lpr_2.6_aarch64.eap", device.UploadedFileName);
        Assert.Equal("lpr 2.6.0 and 2.4.0, aarch64 and aarch64 and armv7hf", StepRun.Detail(ctx.Steps, "Read package"));
        Assert.Equal("AXIS OS 12.11.77, aarch64: lpr_2.6_aarch64.eap", StepRun.Detail(ctx.Steps, "Read device info"));
        Assert.Contains(ctx.Logs, l => l.Message.Contains("row 1 of the mapping file", StringComparison.Ordinal));
        Assert.Contains(ctx.Logs, l => l.Message.Contains("SHA-256 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ZIP_that_cannot_be_opened_for_the_install_is_closed_and_nothing_is_changed()
    {
        var files = new ChangingZipFiles(VendorZip());
        var payload = new AcapPayload { Action = AcapAction.Install, ZipFileId = files.Zip.Id }.ToJson();
        var device = new FakeAcapDevice { Firmware = "12.11.77" };
        var ctx = new RecordingContext(device, files);

        await Assert.ThrowsAnyAsync<Exception>(() => Run(ctx, payload));

        Assert.Null(device.UploadedFileName); // nothing was sent to the device
        Assert.True(files.Opened.Count >= 2); // the read of the builds, then the open for the install
        Assert.All(files.Opened, s => Assert.True(s.Closed, "every stream of the uploaded ZIP is closed"));
    }

    /// <summary>An uploaded ZIP that reads well the first time and is damaged afterwards; it records every stream it handed out.</summary>
    private sealed class ChangingZipFiles(byte[] zip) : IUploadedFiles
    {
        public UploadedFile Zip { get; } = new("zip-1", "lpr.zip", zip.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip)));

        public List<TrackedStream> Opened { get; } = [];

        public Task<UploadedFile?> FindAsync(string fileId, CancellationToken ct) => Task.FromResult<UploadedFile?>(fileId == Zip.Id ? Zip : null);

        public Task<Stream> OpenReadAsync(string fileId, CancellationToken ct)
        {
            var stream = new TrackedStream(Opened.Count == 0 ? zip : Encoding.UTF8.GetBytes("no longer a ZIP"));
            Opened.Add(stream);
            return Task.FromResult<Stream>(stream);
        }
    }

    private sealed class TrackedStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task The_plan_names_the_build_of_each_group_and_asks_for_unclear_ones_at_mapping()
    {
        var (files, a, b) = TwoEqualBuilds();
        var v7 = files.Add("hello_v7.eap", Eap("armv7hf"));
        var devices = new List<Oadm.Sdk.Devices.IDeviceInfo>
        {
            new FakeDevice { Apis = AppApi, Architecture = "aarch64", FirmwareVersion = "12.11.77" },
            new FakeDevice { Apis = AppApi, Architecture = "armv7hf", FirmwareVersion = "12.11.77", Model = "M3086-V" },
            new FakeDevice { Apis = AppApi, Architecture = "mips", FirmwareVersion = "12.11.77", Model = "M1075-L" },
        };
        var payload = new AcapPayload { Action = AcapAction.Install, Files = [new(a.Id, a.Sha256), new(b.Id, b.Sha256), new(v7.Id, v7.Sha256)] };

        var unclear = await Plugin.PlanAsync(payload.ToJson(), devices, files, CancellationToken.None);
        var error = Assert.Single(unclear.Errors);
        Assert.Equal("mapping", error.Field);
        Assert.Equal($"Choose the package for P3265-V, AXIS OS 12.11.77, aarch64: hello_a.eap ({a.Id}) or hello_b.eap ({b.Id}). Several packages fit: hello_a.eap, hello_b.eap.", error.Message);

        var chosen = await Plugin.PlanAsync((payload with { Mapping = [new AcapPackageMapping("12.11.77", "aarch64", a.Id, "P3265-V")] }).ToJson(), devices, files, CancellationToken.None);
        Assert.Empty(chosen.Errors);
        Assert.Equal([null, null, "No package for mips."], chosen.Devices.Select(d => d.SkipReason));
        Assert.Equal(
            ["M1075-L, AXIS OS 12.11.77, mips (1 device): No package for mips.", "M3086-V, AXIS OS 12.11.77, armv7hf (1 device): hello_v7.eap", "P3265-V, AXIS OS 12.11.77, aarch64 (1 device): hello_a.eap (chosen)"],
            chosen.Details);
    }

    [Fact]
    public async Task The_plan_of_a_ZIP_reads_it_on_the_server_and_names_each_build()
    {
        var files = new InMemoryUploadedFiles();
        var zip = files.Add("lpr.zip", VendorZip());
        var devices = new List<Oadm.Sdk.Devices.IDeviceInfo>
        {
            new FakeDevice { Apis = AppApi, FirmwareVersion = "12.11.77" },
            new FakeDevice { Apis = AppApi, FirmwareVersion = "10.12.3", Model = "M3086-V" },
            new FakeDevice { Apis = AppApi, FirmwareVersion = "9.80.3", Model = "M3045-V" },
        };

        var plan = await Plugin.PlanAsync(new AcapPayload { Action = AcapAction.Install, ZipFileId = zip.Id }.ToJson(), devices, files, CancellationToken.None);

        Assert.Empty(plan.Errors);
        Assert.Equal(["lpr.zip", "lpr.zip: lpr_2.4_aarch64.eap", "lpr.zip: lpr_2.6_aarch64.eap", "lpr.zip: lpr_2.6_armv7hf.eap"], plan.Files.Select(f => f.Name));
        Assert.All(plan.Files, f => Assert.Equal(64, f.Sha256.Length));
        Assert.Contains("P3265-V, AXIS OS 12.11.77, aarch64 (1 device): lpr_2.6_aarch64.eap (row 1 of the mapping file)", plan.Details);
        Assert.Contains("M3086-V, AXIS OS 10.12.3, aarch64 (1 device): lpr_2.4_aarch64.eap (row 2 of the mapping file)", plan.Details);
        Assert.Equal("lpr_2.4_aarch64.eap from row 2 of the mapping file does not fit: it needs AXIS OS 10.0 - 10.12.", plan.Devices[2].SkipReason); // the file never overrides the range
    }

    [Fact]
    public async Task A_broken_ZIP_is_a_field_error_of_the_plan()
    {
        var files = new InMemoryUploadedFiles();
        var zip = files.Add("lpr.zip", [1, 2, 3]);

        var plan = await Plugin.PlanAsync(new AcapPayload { Action = AcapAction.Install, ZipFileId = zip.Id }.ToJson(), [new FakeDevice()], files, CancellationToken.None);

        Assert.Equal(("zipFileId", "lpr.zip is damaged or not a ZIP file. Nothing was changed."), (plan.Errors[0].Field, plan.Errors[0].Message));
    }

    // ---- The dialog ----

    [Fact]
    public async Task Dialog_shows_per_device_which_file_and_uploads_every_file()
    {
        var arm64 = WriteEap("hello_aarch64.eap", "aarch64");
        var armv7 = WriteEap("hello_armv7hf.eap", "armv7hf");
        var d1 = new FakeDevice();
        var d2 = new FakeDevice { Apis = AppApi, Architecture = "armv7hf" };
        var d3 = new FakeDevice { Apis = AppApi, Architecture = "mips" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(arch: "aarch64");
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "armv7hf");
        ctx.Devices[d3.Id] = FakeDialogContext.State(arch: "mips");
        var vm = new AcapDialogViewModel(ctx, [d1, d2, d3], new FakeMultiPicker(arm64, armv7));
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.True(vm.HasSeveralPackages);
        Assert.False(vm.ShowGroups); // OADM decides every group
        Assert.Equal("2 files", vm.PackageFileName);
        Assert.Contains("Architecture aarch64, armv7hf", vm.PackageDetails, StringComparison.Ordinal);
        Assert.Equal(["hello_aarch64.eap", "hello_armv7hf.eap", "No package for mips."], vm.Compatibility.Select(r => r.Details));
        Assert.Equal("No package", vm.Compatibility[2].Verdict);
        Assert.True(vm.Compatibility[2].IsWarning);
        Assert.Equal(2, vm.CompatibleCount);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal([arm64, armv7], ctx.Uploads);
        var payload = JsonSerializer.Deserialize<AcapPayload>(closed!, AcapPlugin.Json)!;
        Assert.Equal("file-1", payload.FileId);
        Assert.Equal([new AcapPackageFile("file-1", "ABCDEF", "aarch64"), new AcapPackageFile("file-2", "ABCDEF", "armv7hf")], payload.Files);
        Assert.Null(payload.Mapping);
        Assert.Equal(2, AcapPayload.Parse(closed).AllFiles().Count);
    }

    [Fact]
    public async Task Dialog_shows_the_table_only_for_an_unclear_group_and_installs_once_it_is_chosen()
    {
        var a = WriteEap("hello_a.eap", "aarch64");
        var b = WriteEap("hello_b.eap", "aarch64");
        var v7 = WriteEap("hello_v7.eap", "armv7hf");
        var d1 = new FakeDevice();
        var d2 = new FakeDevice { Apis = AppApi, Architecture = "armv7hf", Model = "M3086-V" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(arch: "aarch64");
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "armv7hf");
        var vm = new AcapDialogViewModel(ctx, [d1, d2], new FakeMultiPicker(a, b, v7));
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Groups); // only the unclear group
        Assert.True(vm.ShowGroups);
        Assert.Equal("P3265-V, AXIS OS 12.11.77, aarch64 (1 device)", row.Label);
        Assert.Equal(["hello_a.eap", "hello_b.eap"], row.Choices);
        Assert.True(row.IsChoosable);
        Assert.Null(row.SelectedChoice);
        Assert.Equal(AcapCompatibility.ChoosePackageVerdict, vm.Compatibility[0].Verdict);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Choose the package for P3265-V, AXIS OS 12.11.77, aarch64.", vm.InstallBlockedReason);

        row.SelectedChoice = "hello_b.eap";

        Assert.Equal(0, vm.UnchosenCount);
        Assert.Null(vm.InstallBlockedReason);
        Assert.Equal(("New install", "hello_b.eap"), (vm.Compatibility[0].Verdict, vm.Compatibility[0].Details));
        Assert.True(vm.InstallCommand.CanExecute(null));

        await vm.InstallCommand.ExecuteAsync(null);

        var payload = AcapPayload.Parse(closed);
        Assert.Equal([new AcapPackageMapping("12.11.77", "aarch64", "file-2", "P3265-V")], payload.Mapping);
    }

    [Fact]
    public async Task Dialog_uses_the_mapping_file_of_a_ZIP_and_sends_its_rows_with_file_ids()
    {
        var zip = Path.Combine(_dir, "lpr.zip");
        await File.WriteAllBytesAsync(zip, VendorZip());
        var d1 = new FakeDevice();
        var d2 = new FakeDevice { Apis = AppApi, Model = "M3086-V" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State(arch: "aarch64");
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "aarch64", fw: "10.12.3");
        using var vm = new AcapDialogViewModel(ctx, [d1, d2], new FakeMultiPicker(zip));
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Null(vm.PackageError);
        Assert.True(vm.HasMappingFile);
        Assert.Contains(AcapDialogViewModel.MappingFact, vm.PackageDetails, StringComparison.Ordinal);
        Assert.Equal(["M3086-V, AXIS OS 10.12.3, aarch64 (1 device)", "P3265-V, AXIS OS 12.11.77, aarch64 (1 device)"], vm.Groups.Select(g => g.Label));
        Assert.Equal(["Row 2 of the mapping file", "Row 1 of the mapping file"], vm.Groups.Select(g => g.Source));
        Assert.All(vm.Groups, g => Assert.False(g.IsChoosable));
        Assert.Equal(["lpr_2.6_aarch64.eap", "lpr_2.4_aarch64.eap"], vm.Compatibility.Select(r => r.Details));

        await vm.InstallCommand.ExecuteAsync(null);

        var payload = AcapPayload.Parse(closed);
        Assert.Equal(["file-1", "file-2", "file-3"], payload.AllFiles().Select(f => f.FileId));
        Assert.Equal(["file-2", "file-1"], payload.Rules!.Select(r => r.File));
    }

    [Fact]
    public async Task Dialog_refuses_a_set_that_does_not_fit_together_before_any_upload()
    {
        var a = WriteEap("a.eap", "aarch64");
        var b = WriteEap("b.eap", "aarch64", app: "other");
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakeMultiPicker(a, b));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("b.eap is another application (Hello World). Choose the files of one application.", vm.PackageError);
        Assert.False(vm.HasPackage);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(ctx.Uploads);

        // One fitting file afterwards works as before.
        await vm.LoadPackageAsync(a);
        Assert.Null(vm.PackageError);
        Assert.Equal("a.eap", vm.PackageFileName);
        Assert.False(vm.HasSeveralPackages);
    }

    [Fact]
    public async Task Dialog_refuses_a_ZIP_whose_mapping_file_names_a_missing_file()
    {
        var zip = Path.Combine(_dir, "bad.zip");
        await File.WriteAllBytesAsync(zip, EapBuilder.Zip(("a.eap", Eap("aarch64")), ("b.eap", Eap("armv7hf")), ("oadm-packages.json", Encoding.UTF8.GetBytes("""{"packages":[{"file":"c.eap"}]}"""))));
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        using var vm = new AcapDialogViewModel(ctx, [d1], new FakeMultiPicker(zip));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("oadm-packages.json row 1 names c.eap, which is not among the .eap files.", vm.PackageError);
        Assert.False(vm.HasPackage);
    }

    [Fact]
    public async Task Dialog_names_the_file_that_is_not_a_package()
    {
        var a = WriteEap("a.eap", "aarch64");
        var bad = Path.Combine(_dir, "bad.eap");
        await File.WriteAllTextAsync(bad, "not a package");
        var d1 = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d1.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d1], new FakeMultiPicker(a, bad));
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.StartsWith("bad.eap: ", vm.PackageError, StringComparison.Ordinal);
        Assert.False(vm.HasPackage);
    }
}
