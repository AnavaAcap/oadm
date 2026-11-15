using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// The server runs <see cref="AcapTaskPlugin.ValidatePayload"/> before any task of any client starts, so every payload the
/// Applications dialog can send must pass it. The dialog's own view model is driven here and the exact JSON it completes
/// with is handed to the validator.
/// </summary>
public sealed class DialogPayloadValidationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-acap-dlg-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Today = new(2026, 10, 13);
    private static readonly AcapTaskPlugin Plugin = new();
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    public DialogPayloadValidationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static InstalledApplication App(string name, string version, bool bundled = false, string license = "None") =>
        new() { Name = name, NiceName = name + " app", Vendor = "Acme", Version = version, Status = "Stopped", Bundled = bundled, License = license, ApplicationId = "415090" };

    private string Eap(string name, string version = "1.2.0", string arch = "aarch64")
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "_" + arch + ".eap");
        File.WriteAllBytes(path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: name, version: version, architecture: arch)));
        return path;
    }

    private string KeyFile(string mac)
    {
        var path = Path.Combine(_dir, mac + "-" + Guid.NewGuid().ToString("N")[..6] + ".key");
        File.WriteAllText(path, LicenseKeyTests.Key(mac: mac));
        return path;
    }

    private sealed class KeyPicker(IReadOnlyList<string> paths) : ILicenseKeyPicker
    {
        public Task<IReadOnlyList<string>> PickLicenseKeysAsync() => Task.FromResult(paths);
    }

    private static void AssertAccepted(string? json)
    {
        Assert.False(string.IsNullOrEmpty(json));
        Assert.Empty(Plugin.ValidatePayload(json, Caller));
    }

    [Fact]
    public async Task Start_stop_and_remove_of_a_listed_application_pass()
    {
        var d = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d.Id] = FakeDialogContext.State(apps: [App("hello", "1.0.0"), App("axis_app-2", "2.0")]);
        foreach (var index in new[] { 0, 1 })
        {
            foreach (var action in new[] { "start", "stop", "remove" })
            {
                var vm = new AcapDialogViewModel(ctx, [d], new FakePicker(null));
                await vm.InitializeAsync();
                string? closed = null;
                vm.CloseRequested += (_, p) => closed = p;
                vm.SelectedApplication = vm.Applications[index];
                switch (action)
                {
                    case "start":
                        vm.StartCommand.Execute(null);
                        break;
                    case "stop":
                        vm.StopCommand.Execute(null);
                        break;
                    default:
                        vm.RemoveCommand.Execute(null);
                        vm.ConfirmRemoveCommand.Execute(null);
                        break;
                }

                AssertAccepted(closed);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task An_install_passes_with_each_option_and_with_a_downgrade(bool allowDowngrade, bool startAfter)
    {
        var d = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d.Id] = FakeDialogContext.State(apps: [App("hello", allowDowngrade ? "2.0.0" : "1.0.0")]);
        var vm = new AcapDialogViewModel(ctx, [d], new FakePicker(Eap("hello")));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.PickPackageCommand.ExecuteAsync(null);
        vm.AllowDowngrade = allowDowngrade;
        vm.StartAfterInstall = startAfter;

        await vm.InstallCommand.ExecuteAsync(null);

        AssertAccepted(closed);
        var payload = AcapPayload.Parse(closed);
        Assert.Equal(allowDowngrade, payload.AllowDowngrade);
        Assert.Equal(startAfter, payload.StartAfterInstall);
    }

    [Fact]
    public async Task A_fresh_install_without_any_installed_application_passes()
    {
        var d = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[d.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d], new FakePicker(Eap("hello")));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.PickPackageCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        AssertAccepted(closed);
    }

    [Fact]
    public async Task An_install_of_one_package_per_architecture_passes()
    {
        var a = new FakeDevice { Address = "10.0.0.48" };
        var b = new FakeDevice { Address = "10.0.0.49" };
        var ctx = new FakeDialogContext();
        ctx.Devices[a.Id] = FakeDialogContext.State(arch: "aarch64");
        ctx.Devices[b.Id] = FakeDialogContext.State(arch: "armv7hf");
        var picker = new FakeMultiPicker(Eap("hello", arch: "aarch64"), Eap("hello", arch: "armv7hf"));
        var vm = new AcapDialogViewModel(ctx, [a, b], picker);
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.PickPackageCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        AssertAccepted(closed);
        Assert.Equal(2, AcapPayload.Parse(closed).AllFiles().Count);
    }

    [Fact]
    public async Task An_install_with_license_keys_passes()
    {
        var d = new FakeDevice { Serial = "B8A44F631339" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d.Id] = FakeDialogContext.State();
        var vm = new AcapDialogViewModel(ctx, [d], new FakePicker(Eap("ax_msf", "1.0.0"))) { Today = () => Today };
        vm.UseKeyPicker(new KeyPicker([KeyFile("B8A44F631339")]));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        await vm.PickPackageCommand.ExecuteAsync(null);
        await vm.PickLicenseKeysCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        AssertAccepted(closed);
        Assert.Single(AcapPayload.Parse(closed).LicenseKeys);
    }

    [Fact]
    public async Task Installing_a_license_on_an_installed_application_passes()
    {
        var d = new FakeDevice { Serial = "B8A44F631339" };
        var ctx = new FakeDialogContext();
        ctx.Devices[d.Id] = FakeDialogContext.State(apps: [App("ax_msf", "1.0", license: "Missing")]);
        var vm = new AcapDialogViewModel(ctx, [d], new FakePicker(null)) { Today = () => Today };
        vm.UseKeyPicker(new KeyPicker([KeyFile("B8A44F631339")]));
        await vm.InitializeAsync();
        string? closed = null;
        vm.CloseRequested += (_, p) => closed = p;
        vm.SelectedApplication = vm.Applications.Single();
        await vm.LicenseSelectionCommand.ExecuteAsync(null);

        vm.InstallLicenseCommand.Execute(null);

        AssertAccepted(closed);
        Assert.Equal(AcapAction.InstallLicense, AcapPayload.Parse(closed).Action);
    }

    /// <summary>The settings action has no button in this dialog; the Applications page sends it with this payload type.</summary>
    [Fact]
    public void A_settings_payload_as_the_applications_page_builds_it_passes()
    {
        AssertAccepted(new AcapPayload
        {
            Action = AcapAction.Settings,
            Application = "hello",
            AppDisplayName = "Hello World",
            Values = new Dictionary<string, string> { ["Mode"] = "fast", ["Empty"] = string.Empty, ["Password"] = "typed-in-oadm" },
            PasswordsAllowed = true,
        }.ToJson());
    }
}
