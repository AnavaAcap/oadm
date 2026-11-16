using System.Text.Json;

using Oadm.Plugins.Acap.Tests;
using Oadm.Plugins.VpnFleet.Client;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.VpnFleet.Tests;

/// <summary>Installs without internet: the packages of a release, uploaded from the page; GitHub is never asked.</summary>
public sealed class LocalPackagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vpnfleet-eap").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task The_task_installs_the_uploaded_package_that_fits_the_camera()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        var uploads = new FakeUploadedFiles();
        var files = Upload(uploads, ("Tailscale_VPN_1_104_7_aarch64.eap", "aarch64"), ("Tailscale_VPN_1_104_7_armv7hf.eap", "armv7hf"));
        var camera = new AppCamera().On("armv7hf", "12.11.77").WithApp("Tailscale_VPN", version: "1.104.6");
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        var ctx = await RunAsync(plugin, camera, uploads, new InstallPayload(VpnKind.Tailscale, "1.104.7", true) { Files = files });

        Assert.Contains("filename=\"Tailscale_VPN_1_104_7_armv7hf.eap\"", Assert.Single(camera.Uploads), StringComparison.Ordinal);
        Assert.Contains($"{InstallTaskPlugin.DownloadStep}: Skipped", StepRun.Lines(ctx.Steps));
        Assert.Equal(("Tailscale_VPN", "1.104.7", "Running"), Assert.Single(camera.Apps));
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task A_camera_no_package_fits_is_not_changed()
    {
        var (plugin, _, _, _) = await TestPlugin.StartAsync();
        var uploads = new FakeUploadedFiles();
        var files = Upload(uploads, ("Tailscale_VPN_1_104_7_aarch64.eap", "aarch64"));
        var camera = new AppCamera().On("armv7hf", "12.11.77");

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => RunAsync(plugin, camera, uploads, new InstallPayload(VpnKind.Tailscale, "1.104.7", false) { Files = files }));

        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(camera.Uploads);
    }

    [Theory]
    [InlineData("other app", "NetBird.eap is a package of")]
    [InlineData("two versions", "The packages have different versions (1.104.7, 1.104.6).")]
    [InlineData("gone", "the upload is gone")]
    [InlineData("not a package", "notes.eap:")]
    public async Task The_install_refuses_packages_that_do_not_belong_together(string problem, string message)
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        var device = server.Add(new FakeDevice(), new AppCamera());
        var call = server.CallAsAllDevices();
        var uploads = new FakeUploadedFiles();
        call.Uploads = uploads;
        var files = problem switch
        {
            "other app" => Upload(uploads, ("Tailscale.eap", "aarch64", "Tailscale_VPN", "1.104.7"), ("NetBird.eap", "armv7hf", VpnApps.Of(VpnKind.NetBird).AppName, "1.104.7")),
            "two versions" => Upload(uploads, ("a.eap", "aarch64", "Tailscale_VPN", "1.104.7"), ("b.eap", "armv7hf", "Tailscale_VPN", "1.104.6")),
            "gone" => [new PackageFile("0123456789abcdef0123456789abcdef", "a.eap")],
            _ => [new PackageFile(uploads.Add("notes.eap", "not a package"u8.ToArray()).Id, "notes.eap")],
        };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => server.InvokeAsync(plugin, call, Fleet.Install,
            JsonSerializer.Serialize(new InstallRequest(VpnKind.Tailscale, [device.Id], false) { Files = files }, Fleet.Json)));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.Empty(server.TaskRuns);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task The_wizard_reads_the_packages_ticks_only_cameras_they_fit_and_uploads_them_at_the_start()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        var devices = new[]
        {
            server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6")),
            server.Add(new FakeDevice { Address = "10.0.0.49", Architecture = "armv7hf", Soc = "Axis Artpec-7" }, new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6")),
        };
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = devices };
        using var page = new VpnFleetViewModel(ctx);
        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(false, [.. devices.Select(d => d.Id)]), Fleet.Json));
        page.Apply(JsonSerializer.Serialize(await NextStateAsync(server, s => !s.Reading && s.Rows.Count > 0), Fleet.Json));

        var asked = github.Requests.Count; // the read asks GitHub for the updates column; the install never does
        var wizard = page.InstallWizard();
        var install = wizard.Install!;
        install.FromFiles = true;
        Assert.Equal("Choose the packages.", wizard.NextBlockedReason);

        var paths = new List<string> { Write("Tailscale_VPN_1_104_7_aarch64.eap", "aarch64", "Tailscale_VPN", "1.104.7"), Write("old.eap", "armv7hf", "Tailscale_VPN", "1.104.6") };
        install.PickPackages = () => Task.FromResult<IReadOnlyList<string>>(paths);
        await install.ChoosePackagesCommand.ExecuteAsync(null);
        Assert.Equal("The packages have different versions (1.104.7, 1.104.6). Choose the packages of one release.", wizard.NextBlockedReason);

        install.RemovePackageCommand.Execute(install.Packages[1]);
        Assert.Null(wizard.NextBlockedReason);
        Assert.Equal("1.104.7", install.LatestVersion);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(["10.0.0.48 Upgrade True", "10.0.0.49 NoPackage False"], wizard.Cameras.All.Select(c => $"{c.Address} {c.Kind} {c.IsChecked}"));

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.StartsWith("From your computer: Tailscale_VPN_1_104_7_aarch64.eap", wizard.Plan[0], StringComparison.Ordinal);
        await wizard.NextCommand.ExecuteAsync(null);

        var run = Assert.Single(server.TaskRuns);
        Assert.Equal([devices[0].Id], run.DeviceIds);
        var sent = Assert.Single(JsonSerializer.Deserialize<InstallPayload>(run.PayloadJson!, Fleet.Json)!.Files!);
        Assert.NotNull(await ctx.Uploaded.FindAsync(sent.FileId, CancellationToken.None));
        Assert.Equal(asked, github.Requests.Count);
    }

    private static List<PackageFile> Upload(FakeUploadedFiles uploads, params (string Name, string Arch)[] packages) =>
        Upload(uploads, [.. packages.Select(p => (p.Name, p.Arch, "Tailscale_VPN", "1.104.7"))]);

    private static List<PackageFile> Upload(FakeUploadedFiles uploads, params (string Name, string Arch, string App, string Version)[] packages) =>
        [.. packages.Select(p => new PackageFile(uploads.Add(p.Name, Eap(p.Arch, p.App, p.Version)).Id, p.Name))];

    private string Write(string name, string arch, string app, string version)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Eap(arch, app, version));
        return path;
    }

    private static byte[] Eap(string arch, string app, string version) =>
        EapBuilder.FromManifest(EapBuilder.Manifest(appName: app, version: version, architecture: arch, friendlyName: app.Replace('_', ' ')));

    private static async Task<FakeTaskContext> RunAsync(VpnFleetPlugin plugin, AppCamera camera, FakeUploadedFiles uploads, InstallPayload payload)
    {
        var ctx = new FakeTaskContext(camera) { Owner = plugin, UploadedFiles = uploads };
        await StepRun.RunAsync(ctx.Steps, () => plugin.InstallTask.ExecuteAsync(ctx, new FakeDevice(), JsonSerializer.Serialize(payload, Fleet.Json), CancellationToken.None));
        return ctx;
    }

    private static async Task<FleetState> NextStateAsync(FakeCoreContext server, Func<FleetState, bool> match)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var item in server.Published.Reader.ReadAllAsync(timeout.Token))
        {
            var state = JsonSerializer.Deserialize<FleetState>(item.PayloadJson!, Fleet.Json)!;
            if (match(state))
            {
                return state;
            }
        }

        throw new TimeoutException();
    }
}
