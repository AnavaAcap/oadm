using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// The SDK rules (README "SDK"): the SDK version a package needs against <c>Properties.EmbeddedDevelopment.Version</c>, the
/// kinds of applications of <c>applications/info.cgi</c>, per build in the chooser, in the preview and in the task's run.
/// </summary>
public sealed class SdkChecksTests
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private static readonly IReadOnlyList<string> P3265Sdks = ["acap3", "acap4-cv", "acap4-native"];

    private static readonly AcapDeviceFacts P3265 = new()
    {
        Architecture = "aarch64", FirmwareVersion = "12.11.77", EmbeddedDevelopmentVersion = "2.18", SupportedSdks = P3265Sdks, AllowUnsigned = true, Model = "P3265-V",
    };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static EapManifest Manifest(string? sdk = "3.0", string version = "1.2.0") =>
        EapReader.Parse(EapBuilder.Manifest(version: version, embeddedSdk: sdk), null);

    private static EapManifest Legacy(string reqEmbDev) => EapReader.Parse(null, EapBuilder.PackageConf(appName: "hello", appType: "aarch64", reqEmbDev: reqEmbDev));

    private static List<AcapBuild> Builds(params (string Name, EapManifest Package)[] packages) => [.. packages.Select(p => new AcapBuild(p.Name, p.Name, p.Package))];

    private static AcapDeviceGroup Group => AcapDeviceGroup.Of(P3265);

    // ---- info.cgi ----

    [Fact]
    public void Recorded_info_cgi_lists_the_three_kinds()
    {
        Assert.Equal(P3265Sdks, ApplicationApiClient.ParseSupportedSdks(Fixture("info-p3265v-12.11.xml")));
    }

    [Fact]
    public void Info_cgi_in_the_element_form_of_the_docs_is_read_too()
    {
        Assert.Equal(["acap3", "acap4-native"],
            ApplicationApiClient.ParseSupportedSdks("<reply result=\"ok\"><supportedSdks><sdk>ACAP3</sdk><sdk> acap4-native </sdk></supportedSdks></reply>"));
    }

    [Theory]
    [InlineData("<reply result=\"error\"><error type=\"10\" message=\"x\"/></reply>")]
    [InlineData("<supportedSdks></supportedSdks>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void An_unreadable_info_cgi_answer_is_unknown(string xml)
    {
        Assert.Null(ApplicationApiClient.ParseSupportedSdks(xml));
    }

    [Fact]
    public async Task Device_facts_read_info_cgi_and_a_device_without_it_is_unknown()
    {
        var device = new FakeAcapDevice();
        Assert.Equal(P3265Sdks, (await new ApplicationApiClient(device).GetDeviceFactsAsync(CancellationToken.None)).SupportedSdks);

        device.InfoXml = null;
        Assert.Null((await new ApplicationApiClient(device).GetDeviceFactsAsync(CancellationToken.None)).SupportedSdks);
        Assert.Empty(device.Writes);
    }

    // ---- SDK version ----

    [Theory]
    [InlineData("2.20", "2.18", false)]
    [InlineData("2.18", "2.18", true)]
    [InlineData("2.9", "2.18", true)] // by numbers: 9 < 18
    [InlineData("2.18.1", "2.18", false)]
    [InlineData("2", "2.18", true)]
    public void A_required_version_below_3_is_compared_by_its_numbers(string required, string device, bool compatible)
    {
        var report = AcapCompatibility.Check(Manifest(required), P3265 with { EmbeddedDevelopmentVersion = device }, null, false);

        Assert.Equal(compatible, report.IsCompatible);
        if (!compatible)
        {
            Assert.Equal($"The application needs a newer ACAP version ({required}) than this device has ({device}). Upgrade AXIS OS first.", Assert.Single(report.Problems));
        }
    }

    [Fact]
    public void Packages_that_say_3_0_install_on_a_device_that_reports_2_18()
    {
        // AXIS OS 12.11 reports 2.18 (recorded) and runs the packages of the ACAP 3 and Native SDKs, which all say 3.0.
        var report = AcapCompatibility.Check(Manifest("3.0"), P3265, null, false);

        Assert.True(report.IsCompatible);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void A_device_that_reports_3_or_later_below_the_package_only_warns()
    {
        var report = AcapCompatibility.Check(Manifest("3.5"), P3265 with { EmbeddedDevelopmentVersion = "3.2" }, null, false);

        Assert.True(report.IsCompatible);
        Assert.Equal("The application needs ACAP version 3.5 and the device reports 3.2. The device decides on install.", Assert.Single(report.Warnings));
        Assert.Empty(AcapCompatibility.Check(Manifest("3.5"), P3265 with { EmbeddedDevelopmentVersion = "3.5" }, null, false).Warnings);
    }

    [Fact]
    public void Missing_versions_mean_no_check()
    {
        Assert.True(AcapCompatibility.Check(Manifest(sdk: null), P3265 with { EmbeddedDevelopmentVersion = "1.0" }, null, false).IsCompatible);

        var unknown = AcapCompatibility.Check(Manifest("2.20"), P3265 with { EmbeddedDevelopmentVersion = null }, null, false);
        Assert.True(unknown.IsCompatible);
        Assert.Equal("The application needs ACAP version 2.20; the device does not report its version.", Assert.Single(unknown.Warnings));
    }

    [Fact]
    public void A_legacy_package_has_the_same_rule_and_sentence()
    {
        var report = AcapCompatibility.Check(Legacy("2.20"), P3265, null, false);

        Assert.Equal("The application needs a newer ACAP version (2.20) than this device has (2.18). Upgrade AXIS OS first.", Assert.Single(report.Problems));
        Assert.True(AcapCompatibility.Check(Legacy("2.12"), P3265, null, false).IsCompatible);
    }

    // ---- Kinds of applications ----

    [Theory]
    [InlineData(new[] { "acap4-cv" }, false)]
    [InlineData(new[] { "acap3" }, true)]
    [InlineData(new[] { "acap4-native" }, true)]
    [InlineData(new[] { "acap3", "acap4-cv", "acap4-native" }, true)]
    public void A_manifest_package_needs_ACAP_3_or_ACAP_4_native(string[] sdks, bool compatible)
    {
        var report = AcapCompatibility.Check(Manifest(), P3265 with { SupportedSdks = sdks }, null, false);

        Assert.Equal(compatible, report.IsCompatible);
        if (!compatible)
        {
            Assert.Equal("This device does not support ACAP 3 or ACAP 4 applications.", Assert.Single(report.Problems));
        }
    }

    [Fact]
    public void No_list_or_a_legacy_package_means_no_check()
    {
        Assert.True(AcapCompatibility.Check(Manifest(), P3265 with { SupportedSdks = null }, null, false).IsCompatible);
        Assert.True(AcapCompatibility.Check(Legacy("2.0"), P3265 with { SupportedSdks = ["acap4-cv"] }, null, false).IsCompatible);
    }

    // ---- The build chooser and the preview ----

    [Fact]
    public void The_chooser_takes_another_build_when_the_newest_needs_a_newer_SDK()
    {
        var builds = Builds(("new.eap", Manifest("2.20", version: "2.0.0")), ("old.eap", Manifest("2.10", version: "1.0.0")));

        Assert.Equal(1, AcapBuildChooser.Choose(builds, Group, device: P3265).Index);

        // Without the device's own read (the plan from cached data) the SDK is not known: the newest, the task checks again.
        Assert.Equal(0, AcapBuildChooser.Choose(builds, Group).Index);
    }

    [Fact]
    public void No_build_with_a_fitting_SDK_is_no_package_with_the_reason()
    {
        var builds = Builds(("a.eap", Manifest("2.20", version: "2.0.0")), ("b.eap", Manifest("2.19", version: "1.0.0")));

        var choice = AcapBuildChooser.Choose(builds, Group, device: P3265);
        Assert.Equal(AcapPick.None, choice.Pick);
        Assert.Equal("No package: The application needs a newer ACAP version (2.20) than this device has (2.18). Upgrade AXIS OS first.", choice.Reason);

        var preview = AcapCompatibility.Preview(builds, choice, P3265, [], false);
        Assert.Equal("No package", preview.Verdict);
        Assert.Equal(choice.Reason, preview.Result);
        Assert.Equal(-1, preview.PackageIndex);
    }

    [Fact]
    public void A_chosen_build_that_does_not_fit_the_SDK_says_why()
    {
        var builds = Builds(("a.eap", Manifest(version: "1.0.0")), ("b.eap", Manifest(version: "1.0.0")));
        var mapping = new[] { AcapPackageMapping.For(Group, "b.eap") };

        var choice = AcapBuildChooser.Choose(builds, Group, mapping: mapping, device: P3265 with { SupportedSdks = ["acap4-cv"] });

        Assert.Equal(AcapPick.None, choice.Pick);
        Assert.Equal("b.eap does not fit: the device does not support ACAP 3 or ACAP 4 applications.", choice.Reason);
    }

    [Fact]
    public void The_preview_of_packages_applies_the_rules_per_build()
    {
        var preview = AcapCompatibility.Preview([Manifest("2.20", version: "2.0.0"), Manifest("2.10", version: "1.0.0")], P3265, [], false, ["new.eap", "old.eap"]);

        Assert.True(preview.IsOk);
        Assert.Equal(1, preview.PackageIndex);

        var single = AcapCompatibility.Preview([Manifest()], P3265 with { SupportedSdks = ["acap4-cv"] }, [], false);
        Assert.True(single.IsError);
        Assert.Equal("This device does not support ACAP 3 or ACAP 4 applications.", single.Result);
    }

    // ---- The task, with a fresh read ----

    [Fact]
    public async Task The_task_refuses_before_the_upload_on_a_device_without_the_kind()
    {
        var device = new FakeAcapDevice { InfoXml = """<supportedSdks><sdk name="acap4-cv"/></supportedSdks>""" };
        var files = new InMemoryUploadedFiles();
        var file = files.Add("hello.eap", EapBuilder.FromManifest(EapBuilder.Manifest()));
        var ctx = new RecordingContext(device, files);
        var payload = new AcapPayload { Action = AcapAction.Install, Application = "hello", FileId = file.Id, Sha256 = file.Sha256 }.ToJson();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None)));

        Assert.Equal("This device does not support ACAP 3 or ACAP 4 applications. Nothing was changed.", ex.Message);
        Assert.Empty(device.Writes);
        Assert.Equal("2.18; computer vision", StepRun.Detail(ctx.Steps, "Read SDK version"));
        Assert.Contains("Check package: Failed", StepRun.Lines(ctx.Steps));
        Assert.Equal("This device does not support ACAP 3 or ACAP 4 applications. Nothing was changed.", StepRun.Detail(ctx.Steps, "Check package"));
    }

    [Fact]
    public async Task The_task_chooses_the_build_that_fits_the_fresh_SDK_version()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var newer = files.Add("hello_new.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "2.0.0", embeddedSdk: "2.20")));
        var older = files.Add("hello_old.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.0.0", embeddedSdk: "2.10")));
        var ctx = new RecordingContext(device, files);
        var payload = new AcapPayload
        {
            Action = AcapAction.Install,
            Application = "hello",
            FileId = newer.Id,
            Sha256 = newer.Sha256,
            Files = [new(newer.Id, newer.Sha256, "aarch64"), new(older.Id, older.Sha256, "aarch64")],
        }.ToJson();

        await StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

        Assert.Equal(["upload"], device.Writes);
        Assert.Equal("hello_old.eap", device.UploadedFileName);
        Assert.Equal("2.18; ACAP 3, computer vision, ACAP 4", StepRun.Detail(ctx.Steps, "Read SDK version"));
    }

    [Fact]
    public async Task The_plan_from_cached_data_runs_and_the_task_skips_a_device_without_a_fitting_build()
    {
        var device = new FakeAcapDevice();
        var files = new InMemoryUploadedFiles();
        var a = files.Add("hello_a.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "2.0.0", embeddedSdk: "2.20")));
        var b = files.Add("hello_b.eap", EapBuilder.FromManifest(EapBuilder.Manifest(version: "1.0.0", embeddedSdk: "2.19")));
        var ctx = new RecordingContext(device, files);
        var payload = new AcapPayload
        {
            Action = AcapAction.Install,
            Application = "hello",
            FileId = a.Id,
            Sha256 = a.Sha256,
            Files = [new(a.Id, a.Sha256, "aarch64"), new(b.Id, b.Sha256, "aarch64")],
        }.ToJson();

        // The cached data has no SDK facts: the plan runs the device with the newest build, the task reads the device.
        var plan = await Plugin.PlanAsync(payload, [new FakeDevice { Apis = [new("application", "1.0")], Architecture = "aarch64", FirmwareVersion = "12.11.77" }], files, CancellationToken.None);
        Assert.Empty(plan.Errors);
        Assert.Null(Assert.Single(plan.Devices).SkipReason);
        Assert.Equal(["P3265-V, AXIS OS 12.11.77, aarch64 (1 device): hello_a.eap"], plan.Details);

        await StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

        Assert.Empty(device.Writes);
        Assert.Equal("No package: The application needs a newer ACAP version (2.20) than this device has (2.18). Upgrade AXIS OS first. Nothing was changed.",
            StepRun.Detail(ctx.Steps, "Read device info"));
    }

    [Theory]
    [InlineData(null, null, "Not reported")]
    [InlineData("2.18", null, "2.18")]
    [InlineData(null, "acap3", "ACAP 3")]
    [InlineData("2.18", "acap3,acap4-native,acap5", "2.18; ACAP 3, ACAP 4, acap5")]
    public void The_step_detail_names_the_kinds_in_plain_words(string? version, string? sdks, string expected)
    {
        Assert.Equal(expected, AcapTaskPlugin.SdkText(version, sdks?.Split(',')));
    }
}
