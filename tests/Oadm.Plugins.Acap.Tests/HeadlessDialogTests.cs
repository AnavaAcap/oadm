using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

using Oadm.Plugins.Acap.Client;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>Minimal host: dark theme variant plus the real OADM theme, like the client app.</summary>
public sealed class HeadlessTestApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new StyleInclude(new Uri("avares://Oadm.Plugins.Acap.Tests/")) { Source = new Uri("avares://Oadm.Client/Themes/OadmTheme.axaml") });
    }
}

public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>Renders the dialog offscreen with the host theme. Set OADM_SCREENSHOT_DIR to write PNGs.</summary>
public sealed class HeadlessDialogTests
{
    [Fact]
    public async Task Dialog_renders_installed_apps_and_package_check()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var dir = Path.Combine(Path.GetTempPath(), "oadm-acap-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var eapPath = Path.Combine(dir, "Missing_Features_3_9_0_aarch64.eap");
        // Synchronous on purpose: awaiting before StartNew moves the test to a pool thread and the
        // session's Dispose then deadlocks.
        File.WriteAllBytes(eapPath,EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", friendlyName: "Missing Features", vendor: "Sample Vendor GmbH", version: "3.9.0", schema: "1.8.0", osMin: "11.11", osMax: "99")));

        var d1 = new FakeDevice { Model = "P3265-V", Address = "10.0.0.48" };
        var d2 = new FakeDevice { Model = "M3086-V", Address = "10.0.0.52", Serial = "B8A44F000052" };
        var ctx = new FakeDialogContext();
        var recorded = ApplicationApiClient.ParseList(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "list-p3265v-12.11.xml")));
        ctx.Devices[d1.Id] = new ListApplicationsResult { Device = new AcapDeviceFacts { Architecture = "aarch64", FirmwareVersion = "12.11.77", AllowUnsigned = true }, Applications = recorded };
        ctx.Devices[d2.Id] = FakeDialogContext.State(arch: "armv7hf", fw: "11.11.124");
        var arm64Path = Path.Combine(dir, "Missing_Features_3_9_1_aarch64.eap");
        var armv7Path = Path.Combine(dir, "Missing_Features_3_9_1_armv7hf.eap");
        File.WriteAllBytes(arm64Path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", friendlyName: "Missing Features", vendor: "Sample Vendor GmbH", version: "3.9.1", schema: "1.3", architecture: "aarch64")));
        File.WriteAllBytes(armv7Path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", friendlyName: "Missing Features", vendor: "Sample Vendor GmbH", version: "3.9.1", schema: "1.3", architecture: "armv7hf")));
        var zipPath = Path.Combine(dir, "Missing_Features_3_9_1.zip");
        File.WriteAllBytes(zipPath, EapBuilder.Zip(("Missing_Features_3_9_1_aarch64.eap", File.ReadAllBytes(arm64Path)), ("Missing_Features_3_9_1_armv7hf.eap", File.ReadAllBytes(armv7Path)), ("readme.txt", [1, 2, 3])));
        var longZipPath = Path.Combine(dir, "Sample_Vendor_Missing_Features_for_AXIS_OS_12_signed_release_build_3_9_1.zip");
        File.WriteAllBytes(longZipPath, EapBuilder.Zip(("Missing_Features_3_9_1_aarch64.eap", File.ReadAllBytes(arm64Path))));
        var emptyZipPath = Path.Combine(dir, "Manual.zip");
        File.WriteAllBytes(emptyZipPath, EapBuilder.Zip(("readme.txt", [1, 2, 3])));
        var sameAgainPath = Path.Combine(dir, "Missing_Features_3_9_1_aarch64_signed.eap");
        File.WriteAllBytes(sameAgainPath, File.ReadAllBytes(arm64Path));
        var otherAppPath = Path.Combine(dir, "Other_App_1_0_aarch64.eap");
        File.WriteAllBytes(otherAppPath, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "other", friendlyName: "Other App", version: "1.0.0", schema: "1.3", architecture: "aarch64")));
        byte[] Build(string arch, string version, string min, string? max) =>
            EapBuilder.FromManifest(EapBuilder.Manifest(appName: "ax_msf", friendlyName: "Missing Features", vendor: "Sample Vendor GmbH", version: version, schema: "1.3", architecture: arch, osMin: min, osMax: max));
        var mappedZipPath = Path.Combine(dir, "Missing_Features_all_tracks.zip");
        File.WriteAllBytes(mappedZipPath, EapBuilder.Zip(
            ("Missing_Features_3_8_4_aarch64_LTS.eap", Build("aarch64", "3.8.4", "10.0", "10.12")),
            ("Missing_Features_3_9_1_aarch64.eap", Build("aarch64", "3.9.1", "11.0", null)),
            ("Missing_Features_3_9_1_armv7hf.eap", Build("armv7hf", "3.9.1", "11.0", null)),
            ("oadm-packages.json", System.Text.Encoding.UTF8.GetBytes("""{"packages":[{"file":"Missing_Features_3_9_1_aarch64.eap","models":["P3265-V"]},{"file":"Missing_Features_3_9_1_armv7hf.eap","architecture":"armv7hf"}]}"""))));
        var keyPath = Path.Combine(Path.GetTempPath(), "oadm-acap-headless-" + Guid.NewGuid().ToString("N") + ".key");
        File.WriteAllText(keyPath, LicenseKeyTests.Key());

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var (rows, compat) = await session.Dispatch(async () =>
        {
            var window = new AcapWindow();
            var vm = new AcapDialogViewModel(ctx, [d1, d2], new FakePicker(eapPath)) { Today = () => new DateOnly(2026, 10, 13) };
            window.Attach(vm);
            vm.UseKeyPicker(new KeyPicker([keyPath]));
            window.Show();
            await vm.InitializeAsync();
            vm.SelectedApplication = vm.Applications.First(a => a.PackageName == "ax_msf");
            Pump();
            Capture(window, outDir, "acap-dialog-apps.png");

            await vm.PickPackageCommand.ExecuteAsync(null);
            Pump();
            Capture(window, outDir, "acap-dialog-install.png");

            await vm.PickLicenseKeysCommand.ExecuteAsync(null);
            Pump();
            Capture(window, outDir, "acap-dialog-install-keys.png");

            await vm.LicenseSelectionCommand.ExecuteAsync(null);
            Pump();
            Capture(window, outDir, "acap-dialog-license.png");
            Assert.Equal(["Key found", "No key"], vm.LicenseRows.Select(r => r.Verdict));

            vm.RemoveCommand.Execute(null);
            Pump();
            Capture(window, outDir, "acap-dialog-remove-confirm.png");
            vm.CancelRemoveCommand.Execute(null);

            // One package per architecture: each device gets its build.
            await vm.LoadPackagesAsync([arm64Path, armv7Path]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-several.png");
            Assert.Equal(["Missing_Features_3_9_1_aarch64.eap", "Missing_Features_3_9_1_armv7hf.eap"], vm.Compatibility.Select(r => r.Details));
            Assert.False(vm.ShowGroups);

            // Two aarch64 builds OADM cannot tell apart: the table asks, Install waits for the choice.
            await vm.LoadPackagesAsync([arm64Path, sameAgainPath, armv7Path]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-unclear.png");
            Assert.True(vm.ShowGroups);
            Assert.False(vm.InstallCommand.CanExecute(null));
            vm.Groups[0].SelectedChoice = vm.Groups[0].Choices[1];
            Pump();
            Capture(window, outDir, "acap-dialog-install-chosen.png");
            Assert.True(vm.InstallCommand.CanExecute(null));

            // A vendor's ZIP with builds per AXIS OS and its mapping file.
            await vm.LoadPackagesAsync([mappedZipPath]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-mapping.png");
            Assert.True(vm.HasMappingFile);
            Assert.Null(vm.PackageError);

            await vm.LoadPackagesAsync([arm64Path, otherAppPath]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-refused.png");
            Assert.NotNull(vm.PackageError);

            // A vendor's ZIP with one package per architecture, then one the dialog refuses.
            await vm.LoadPackagesAsync([zipPath]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-zip.png");
            Assert.Equal("Missing_Features_3_9_1.zip  ·  2 files", vm.PackageFileName);
            Assert.Equal(["Missing_Features_3_9_1_aarch64.eap", "Missing_Features_3_9_1_armv7hf.eap"], vm.Compatibility.Select(r => r.Details));

            await vm.LoadPackagesAsync([emptyZipPath]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-zip-refused.png");
            Assert.Equal("Manual.zip has no .eap files.", vm.PackageError);

            // The widest row: a long name next to a loose file, the count still in the button.
            await vm.LoadPackagesAsync([longZipPath, armv7Path]);
            Pump();
            Capture(window, outDir, "acap-dialog-install-zip-long.png");
            Assert.EndsWith("and 1 more  ·  2 files", vm.PackageFileName, StringComparison.Ordinal);

            await vm.LoadPackagesAsync([zipPath]);
            var unpacked = vm.PackageFiles.Folder;
            Assert.True(Directory.Exists(unpacked));
            var result = (vm.Applications.Count, vm.Compatibility.Count);
            window.Close();
            Assert.False(Directory.Exists(unpacked)); // closing the window deletes what the ZIP unpacked

            // Install licenses of the Applications page: the dialog opens on its license part.
            var licenseCtx = new FakeDialogContext { StartView = AcapDialogViewModel.LicenseView };
            licenseCtx.Devices[d1.Id] = ctx.Devices[d1.Id];
            licenseCtx.Devices[d2.Id] = ctx.Devices[d2.Id];
            var licenseWindow = new AcapWindow();
            var licenseVm = new AcapDialogViewModel(licenseCtx, [d1, d2], new FakePicker(eapPath)) { Today = () => new DateOnly(2026, 10, 13) };
            licenseWindow.Attach(licenseVm);
            licenseVm.UseKeyPicker(new KeyPicker([keyPath]));
            licenseWindow.Show();
            await licenseVm.InitializeAsync();
            Pump();
            Assert.True(licenseVm.IsLicenseMode);
            Capture(licenseWindow, outDir, "followup-acap-license-start.png");
            licenseWindow.Close();
            return result;
        }, CancellationToken.None);

        Assert.Equal(8, rows);
        Assert.Equal(2, compat);
        Directory.Delete(dir, recursive: true);
        File.Delete(keyPath);
    }

    private sealed class KeyPicker(IReadOnlyList<string> paths) : ILicenseKeyPicker
    {
        public Task<IReadOnlyList<string>> PickLicenseKeysAsync() => Task.FromResult(paths);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (string.IsNullOrEmpty(outDir))
        {
            frame.Dispose();
            return;
        }

        Directory.CreateDirectory(outDir);
        using (frame)
        {
            frame.Save(Path.Combine(outDir, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
}
