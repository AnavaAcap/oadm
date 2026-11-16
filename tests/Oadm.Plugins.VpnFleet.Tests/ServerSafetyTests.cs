using System.Net;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Acap.Tests;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VpnFleet.Tests;

/// <summary>
/// The device safety of the server part: API checks before writing, the package checks of Applications (ACAP), the approved
/// version, the hub's peers, secrets in errors, OpenVPN profiles, the WireGuard set up and the GitHub client.
/// </summary>
public sealed class ServerSafetyTests
{
    private const string TailscaleOnline = """{"BackendState":"Running","Self":{"HostName":"cam","Online":true,"TailscaleIPs":["100.64.0.9"]}}""";

    private static readonly string[] TailscaleAssets =
    [
        "signed_Tailscale_VPN_1_104_7_aarch64.eap",
        "signed_Tailscale_VPN_1_104_7_armv7hf.eap",
        "Tailscale_VPN_1_104_7_aarch64_root.eap",
        "Tailscale_VPN_1_104_7_armv7hf_acap3.eap",
    ];

    // ---- 1: the device's APIs before the first write ----

    [Fact]
    public void The_tasks_run_only_on_devices_that_offer_their_APIs()
    {
        var plugin = new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());
        var none = new FakeDevice();
        var both = new FakeDevice { Apis = [new("param-cgi", "1.0"), new("application", "1.2")] };
        var paramOnly = new FakeDevice { Apis = [new("param-cgi", "1.0")] };

        Assert.False(plugin.InstallTask.CanRun(none));
        Assert.False(plugin.ConnectTask.CanRun(none));
        Assert.True(plugin.InstallTask.CanRun(both));
        Assert.True(plugin.ConnectTask.CanRun(both));
        Assert.False(plugin.ConnectTask.CanRun(paramOnly));
    }

    [Fact]
    public async Task Install_reads_the_API_list_before_the_upload()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera { Apis = [new("param-cgi", "1.0")] };
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", false)));

        Assert.Empty(camera.Uploads);
        Assert.Empty(camera.Controls);
    }

    [Fact]
    public async Task Connect_reads_the_API_list_before_the_first_write()
    {
        var camera = new AppCamera { Apis = [new("param-cgi", "1.0")] }.WithApp(VpnApps.Tailscale.AppName);

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() =>
            ConnectTests.RunTaskAsync(camera, new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.Tailscale, AuthKey = "tskey-auth-1" } }));

        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task Save_reads_the_API_list_before_writing()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera { Apis = [] }.WithApp("Tailscale_VPN");
        camera.Parameters["Tailscale_VPN.AcceptDNS"] = "false";
        var device = server.Add(new FakeDevice(), camera);

        await Assert.ThrowsAnyAsync<Exception>(() => SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.Tailscale, new Dictionary<string, string> { ["AcceptDNS"] = "true" })));

        Assert.Equal(0, camera.Writes);
        Assert.Contains(camera.Requests, r => r.Contains("apidiscovery", StringComparison.Ordinal));
    }

    // ---- 2: every package is checked like Applications (ACAP) does ----

    [Fact]
    public async Task A_GitHub_package_of_another_app_is_never_installed()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        github.Files["signed_Tailscale_VPN_1_104_7_aarch64.eap"] = FakeGitHub.Package("NetBird_VPN", "1.104.7", "x_aarch64.eap");
        var camera = new AppCamera();
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", false)));

        Assert.Contains("not Tailscale", ex.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(camera.Uploads);
    }

    [Fact]
    public async Task A_package_for_a_newer_AXIS_OS_is_never_installed()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().On("aarch64", "11.11.100"); // the package's manifest needs AXIS OS 12.0
        camera.Installs = ("Tailscale_VPN", "1.104.7");
        var ctx = new FakeTaskContext(camera) { Owner = plugin };

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.InstallTask.ExecuteAsync(ctx, new FakeDevice(), Json(new InstallPayload(VpnKind.Tailscale, "1.104.7", false)), CancellationToken.None)));

        Assert.Contains("needs AXIS OS 12.0 or later", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{InstallTaskPlugin.CheckPackageStep}: Failed", StepRun.Lines(ctx.Steps));
        Assert.Empty(camera.Uploads);
    }

    [Fact]
    public async Task An_uploaded_package_that_runs_as_root_is_never_installed_on_AXIS_OS_12()
    {
        var (plugin, _, _, _) = await TestPlugin.StartAsync();
        var uploads = new FakeUploadedFiles();
        const string name = "Tailscale_VPN_1_104_7_aarch64_root.eap";
        var files = new List<PackageFile> { new(uploads.Add(name, FakeGitHub.Package("Tailscale_VPN", "1.104.7", name)).Id, name) };
        var camera = new AppCamera();

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", false) { Files = files }, uploads));

        Assert.Contains("runs as root", ex.Message, StringComparison.Ordinal);
        Assert.Empty(camera.Uploads);
    }

    [Fact]
    public async Task A_device_whose_version_cannot_be_read_is_upgraded()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().WithApp("Tailscale_VPN", status: "Stopped", version: "unknown");
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        await RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", true));

        Assert.Single(camera.Uploads);
        Assert.Equal(("Tailscale_VPN", "1.104.7", "Running"), Assert.Single(camera.Apps));
    }

    // ---- 3: a whole release from disk ----

    [Fact]
    public async Task A_whole_release_from_disk_installs_the_signed_build()
    {
        var (plugin, _, _, _) = await TestPlugin.StartAsync();
        var uploads = new FakeUploadedFiles();
        string[] names =
        [
            "Tailscale_VPN_1_104_7_aarch64.eap", "signed_Tailscale_VPN_1_104_7_aarch64.eap",
            "Tailscale_VPN_1_104_7_armv7hf.eap", "signed_Tailscale_VPN_1_104_7_armv7hf.eap",
        ];
        var files = names.Select(n => new PackageFile(uploads.Add(n, FakeGitHub.Package("Tailscale_VPN", "1.104.7", n)).Id, n)).ToList();
        var camera = new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6");
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        await RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.7", true) { Files = files }, uploads);

        Assert.Contains("filename=\"signed_Tailscale_VPN_1_104_7_aarch64.eap\"", Assert.Single(camera.Uploads), StringComparison.Ordinal);
    }

    [Fact]
    public void The_signed_twin_wins_and_other_ties_stay_unclear()
    {
        static AcapBuild Build(string name, string arch) =>
            new(name, name, EapReader.Parse(EapBuilder.Manifest(appName: "Tailscale_VPN", version: "1.104.7", architecture: arch), null));
        var group = new AcapDeviceGroup("P3265-V", "12.11.77", "aarch64");

        var twins = LocalPackages.Choose([Build("a_aarch64.eap", "aarch64"), Build("signed_a_aarch64.eap", "aarch64")], group);
        Assert.Equal((AcapPick.Chosen, 1), (twins.Pick, twins.Index));

        var strangers = LocalPackages.Choose([Build("a_aarch64.eap", "aarch64"), Build("b_aarch64.eap", "aarch64")], group);
        Assert.Equal(AcapPick.Unclear, strangers.Pick);
    }

    // ---- 4: the approved version only ----

    [Fact]
    public async Task Install_never_takes_another_version_than_the_approved_one()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera().WithApp("Tailscale_VPN", version: "1.104.5");
        camera.Installs = ("Tailscale_VPN", "1.104.7");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunInstallAsync(plugin, camera, new InstallPayload(VpnKind.Tailscale, "1.104.6", true)));

        Assert.Equal("The newest release of Tailscale is now 1.104.7, not the approved 1.104.6. Start the install again. Nothing was changed.", ex.Message);
        Assert.Empty(camera.Uploads);
    }

    // ---- 5: one change of a hub's peers at a time ----

    [Fact]
    public async Task Two_runs_on_one_hub_never_lose_a_peer()
    {
        var plugin = new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());
        var hub = new AppCamera().WithApp(VpnApps.WireGuardServerAppName);
        hub.Parameters["WireGuard_Server.Peers"] = "";
        hub.Parameters["WireGuard_Server.Enabled"] = "yes";
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        hub.BeforeList = async () =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                held.TrySetResult();
                await release.Task; // the first run holds its read here
            }
        };
        var device = new FakeDevice();
        var a = new HubPeer("CAM-A", WireGuardKeys.Generate().PublicKey, "10.66.0.2");
        var b = new HubPeer("CAM-B", WireGuardKeys.Generate().PublicKey, "10.66.0.3");
        Task Run(HubPeer peer)
        {
            var ctx = new FakeTaskContext(hub) { Owner = plugin };
            var json = Json(new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.WireGuard }, HubPeers = [peer] });
            return Task.Run(() => StepRun.RunAsync(ctx.Steps, () => plugin.ConnectTask.ExecuteAsync(ctx, device, json, CancellationToken.None)));
        }

        var first = Run(a);
        await Eventually.TrueAsync(() => held.Task.IsCompleted, "the first run reads the peers");
        var second = Run(b);
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, reads);
        Assert.Equal([a, b], WireGuardAddressing.ParsePeers(hub.Parameters["root.WireGuard_Server.Peers"]));
    }

    // ---- 6: secrets never in an error, a log line or a step ----

    [Fact]
    public async Task A_refused_secret_is_named_never_shown()
    {
        const string secret = "tskey-auth-secret123";
        var camera = new AppCamera().WithApp(VpnApps.Tailscale.AppName, status: "Stopped");
        var inner = camera.Respond!;
        camera.Respond = (request, body) => request.RequestUri!.OriginalString.StartsWith(DeviceVpn.ParamPath, StringComparison.Ordinal) && body?.Contains("action=update", StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"# Error: Error setting 'root.Tailscale_VPN.AuthKey' to '{secret}'!") }
            : inner(request, body);
        var plugin = new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());
        var ctx = new FakeTaskContext(camera) { Owner = plugin };
        var payload = new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.Tailscale, AuthKey = secret } };

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.ConnectTask.ExecuteAsync(ctx, new FakeDevice(), Json(payload), CancellationToken.None)));

        Assert.Equal("The device refused the setting AuthKey.", ex.Message);
        Assert.DoesNotContain(ctx.LogLines, l => l.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(ctx.Steps.Snapshot(), s => (s.Detail ?? "").Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(ctx.Warnings, w => w.Contains(secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("# Error: Error setting 'root.Other.Key' to 'x'!", 200, "The device refused the settings AuthKey, CustomServer.")]
    [InlineData("secret echoed", 500, "The device refused the settings AuthKey, CustomServer (HTTP 500).")]
    public void A_refusal_names_only_settings_that_were_sent(string body, int status, string expected) =>
        Assert.Equal(expected, DeviceVpn.RefusedText(["root.Tailscale_VPN.AuthKey", "root.Tailscale_VPN.CustomServer"], body, status));

    // ---- 7: OpenVPN profiles ----

    [Theory]
    [InlineData("client\nremote vpn.example.com 1194\nscript-security 2\n", "script-security")]
    [InlineData("client\nremote vpn.example.com 1194\nup /bin/sh\n", "up")]
    [InlineData("client\nremote vpn.example.com 1194\n  --route-up /tmp/x\n", "route-up")]
    [InlineData("client\nremote vpn.example.com 1194\nsetenv opt down /tmp/x\n", "down")]
    [InlineData("client\nremote vpn.example.com 1194\nPLUGIN /lib/x.so\n", "plugin")]
    [InlineData("<connection>\nremote vpn.example.com 1194\nauth-user-pass-verify /tmp/x via-env\n</connection>\n", "auth-user-pass-verify")]
    public void A_profile_that_runs_commands_is_refused(string profile, string directive) =>
        Assert.Equal($"The profile runs commands on the camera ({directive}). Remove that line.", ConnectRules.ProfileProblem(profile));

    [Fact]
    public void Comments_and_inline_data_never_count_as_commands() =>
        Assert.Null(ConnectRules.ProfileProblem("client\n# up /bin/sh\n; down x\nremote vpn.example.com 1194\n<ca>\nup\ndown\n</ca>\nsetenv FOO bar\n"));

    [Fact]
    public async Task A_refused_OpenVpn_write_never_uploads_the_profile()
    {
        var camera = new AppCamera().WithApp(VpnApps.OpenVpn.AppName);
        camera.Files[VpnApps.OpenVpn.LocalPath("api/profile")] = "OK";
        var inner = camera.Respond!;
        camera.Respond = (request, body) => request.RequestUri!.OriginalString.StartsWith(DeviceVpn.ParamPath, StringComparison.Ordinal) && body?.Contains("action=update", StringComparison.Ordinal) == true
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("# Error: Error setting 'root.OpenVPN_VPN.Password' to 'p'!") }
            : inner(request, body);
        var payload = new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.OpenVpn, Profile = "client\nremote vpn.example.com 1194\n", UserName = "cam", Password = "p" } };

        await Assert.ThrowsAsync<VpnDeviceException>(() => ConnectTests.RunTaskAsync(camera, payload));

        Assert.DoesNotContain(camera.Posts, p => p.Path == VpnApps.OpenVpn.LocalPath("api/profile"));
    }

    // ---- 8: one parse per run ----

    [Fact]
    public void A_payload_is_parsed_once_per_run()
    {
        var cameras = Enumerable.Range(0, 1000).ToDictionary(_ => Guid.NewGuid(), i => new WireGuardCamera(null, $"10.66.{i / 250}.{(i % 250) + 2}/16") { PublicKey = "k" });
        var json = Json(new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = Guid.NewGuid() }, Cameras = cameras });

        var first = ConnectPayload.ParseShared(json);

        Assert.Same(first, ConnectPayload.ParseShared(json));
        Assert.Equal(1000, first.Cameras!.Count);
    }

    // ---- 9: "Connected" only from the new settings ----

    [Fact]
    public async Task An_old_connected_status_does_not_count_after_the_save()
    {
        var start = DateTimeOffset.Parse("2026-11-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var time = new FakeTimeProvider(start);
        var plugin = new VpnFleetPlugin(time, new FakeGitHub());
        plugin.ConnectTask.SettleTime = TimeSpan.FromSeconds(10);
        var camera = new AppCamera().WithApp(VpnApps.Tailscale.AppName); // running, connected with the old settings
        camera.Files[VpnApps.Tailscale.LocalPath("status.json")] = TailscaleOnline;
        var ctx = new FakeTaskContext(camera) { Owner = plugin };
        var json = Json(new ConnectPayload { Settings = new ConnectRequest { Kind = VpnKind.Tailscale, AuthKey = "tskey-auth-new" } });

        var run = Task.Run(() => StepRun.RunAsync(ctx.Steps, () => plugin.ConnectTask.ExecuteAsync(ctx, new FakeDevice(), json, CancellationToken.None)));
        await Eventually.TrueAsync(() => camera.Requests.Any(r => r.Contains("status.json", StringComparison.Ordinal)), "the first status read");

        Assert.False(run.IsCompleted); // a read right after the restart may be the old one
        Assert.Equal(["restart Tailscale_VPN"], camera.Controls);
        await Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return run.IsCompleted;
        }, "the task ends once the app had time to restart");
        await run;
        Assert.True(time.GetUtcNow() - start >= TimeSpan.FromSeconds(10));
        Assert.Equal("Connected, 100.64.0.9", StepRun.Detail(ctx.Steps, ConnectTaskPlugin.WaitStep));
    }

    // ---- 10: Set up again keeps working tunnels; cameras wait for their hub ----

    [Fact]
    public async Task A_camera_changes_nothing_when_its_hub_task_failed()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var hubTask = (await server.RunAsync(ConnectTaskPlugin.PluginId, [Guid.NewGuid()], "{}", "admin", CancellationToken.None))[0];
        server.EndTask(hubTask, TaskEndState.Failed, "The device refused the setting Peers.");
        var camera = ConnectTests.WireGuardApp();
        var device = new FakeDevice();

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() => ConnectTests.RunTaskAsync(camera, WireGuardPayload(device.Id, hubTask), plugin, device, TimeSpan.Zero));

        Assert.Equal("The WireGuard server did not add the cameras: The device refused the setting Peers. Nothing was changed.", ex.Message);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task A_camera_waits_for_its_hub_task_before_it_writes()
    {
        var (plugin, server, _, time) = await TestPlugin.StartAsync();
        plugin.ConnectTask.ConnectWait = TimeSpan.Zero;
        plugin.ConnectTask.SettleTime = TimeSpan.Zero;
        var hubTask = (await server.RunAsync(ConnectTaskPlugin.PluginId, [Guid.NewGuid()], "{}", "admin", CancellationToken.None))[0];
        var camera = ConnectTests.WireGuardApp();
        var device = new FakeDevice();
        var ctx = new FakeTaskContext(camera) { Owner = plugin };

        var run = Task.Run(() => StepRun.RunAsync(ctx.Steps, () => plugin.ConnectTask.ExecuteAsync(ctx, device, Json(WireGuardPayload(device.Id, hubTask)), CancellationToken.None)));
        await Eventually.TrueAsync(() => ctx.Steps.Snapshot().Any(s => s.Name == ConnectTaskPlugin.HubStep && s.State == TaskStepState.Running), "the camera waits for the hub");

        Assert.Equal(0, camera.Writes);
        server.EndTask(hubTask);
        await Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return run.IsCompleted;
        }, "the camera goes on once the hub has it");
        await run;
        Assert.Equal(1, camera.Writes);
    }

    // ---- 11: addresses only for cameras with the app; removed devices free theirs ----

    [Fact]
    public async Task Only_cameras_with_the_app_get_an_address_and_removed_devices_free_theirs()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var (hub, hubCamera, _) = ConnectTests.AddHub(server);
        var oldKey = WireGuardKeys.Generate().PublicKey;
        hubCamera.Parameters["WireGuard_Server.Peers"] = $"OLDCAM,{oldKey},10.66.0.2";
        var removed = new WireGuardRecord(Guid.NewGuid(), "OLDCAM", oldKey, "10.66.0.2", "10.66.0.0/24", hub.Id);
        server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey] = JsonSerializer.Serialize(new[] { removed }, Fleet.Json);
        var a = server.Add(new FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000001" }, ConnectTests.WireGuardApp());
        var noApp = server.Add(new FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" }, new AppCamera());

        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Connect,
            Json(new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = hub.Id, DeviceIds = [a.Id, noApp.Id] }));

        var hubRun = JsonSerializer.Deserialize<ConnectPayload>(server.TaskRuns[0].PayloadJson!, Fleet.Json)!;
        Assert.Equal(["ACCC8E000001 10.66.0.2"], hubRun.HubPeers!.Select(p => $"{p.Name} {p.Address}"));
        Assert.Equal("OLDCAM", Assert.Single(hubRun.RemovedPeers!).Name);
        var cameraRun = JsonSerializer.Deserialize<ConnectPayload>(server.TaskRuns[1].PayloadJson!, Fleet.Json)!;
        Assert.Equal([a.Id, noApp.Id], server.TaskRuns[1].DeviceIds); // its task says the app is missing
        Assert.Equal([a.Id], cameraRun.Cameras!.Keys);
        var stored = JsonSerializer.Deserialize<List<WireGuardRecord>>(server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey]!, Fleet.Json)!;
        Assert.Equal([a.Id], stored.Select(r => r.DeviceId));

        // The hub's task takes the peer of the removed device off the hub.
        await ConnectTests.RunTaskAsync(hubCamera, hubRun, plugin, hub);
        Assert.Equal(["ACCC8E000001"], WireGuardAddressing.ParsePeers(hubCamera.Parameters["root.WireGuard_Server.Peers"]).Select(p => p.Name));
    }

    // ---- 12: a camera's address or a hub's values changed on the page ----

    [Fact]
    public async Task A_new_tunnel_address_goes_to_the_record_and_the_hub()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var hub = server.Add(new FakeDevice { Address = "10.0.0.10" }, new AppCamera());
        var (device, camera) = CameraBehindHub(server, hub.Id, out var key);

        var reply = await SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.WireGuard, new Dictionary<string, string> { ["ClientIP"] = "10.66.0.7/24" }));

        Assert.Equal("10.66.0.7/24", camera.Parameters["root.wireguardconfig.ClientIP"]);
        var peer = Assert.Single(JsonSerializer.Deserialize<ConnectPayload>(Assert.Single(server.TaskRuns).PayloadJson!, Fleet.Json)!.HubPeers!);
        Assert.Equal(new HubPeer("ACCC8E000001", key, "10.66.0.7"), peer);
        Assert.Equal("The hub camera gets the new tunnel address (see the task list).", reply.Note);
        Assert.Equal("10.66.0.7", Records(server).Single(r => r.DeviceId == device.Id).Address);
    }

    [Fact]
    public async Task A_tunnel_address_of_another_camera_is_refused_before_writing()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var hub = server.Add(new FakeDevice { Address = "10.0.0.10" }, new AppCamera());
        var (device, camera) = CameraBehindHub(server, hub.Id, out _);
        var records = Records(server);
        records.Add(new WireGuardRecord(Guid.NewGuid(), "OTHER", WireGuardKeys.Generate().PublicKey, "10.66.0.7", "10.66.0.0/24", hub.Id));
        server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey] = JsonSerializer.Serialize(records, Fleet.Json);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            SaveAsync(plugin, server, new SaveSettingsRequest(device.Id, VpnKind.WireGuard, new Dictionary<string, string> { ["ClientIP"] = "10.66.0.7/24" })));

        Assert.Equal("Another camera of this WireGuard server has the tunnel address 10.66.0.7.", ex.Message);
        Assert.Equal(0, camera.Writes);
    }

    [Fact]
    public async Task A_hub_change_says_its_cameras_need_set_up_again()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var (hub, _, _) = ConnectTests.AddHub(server);
        CameraBehindHub(server, hub.Id, out _);

        var reply = await SaveAsync(plugin, server, new SaveSettingsRequest(hub.Id, VpnKind.WireGuardServer, new Dictionary<string, string> { ["ListenPort"] = "51821" }));

        Assert.Equal("The cameras of this WireGuard server still have its old values. Set them up again.", reply.Note);
        var none = await SaveAsync(plugin, server, new SaveSettingsRequest(hub.Id, VpnKind.WireGuardServer, new Dictionary<string, string> { ["Enabled"] = "yes" }));
        Assert.Null(none.Note);
    }

    // ---- 14: the smaller ones ----

    [Fact]
    public void The_audit_detail_takes_missing_fields()
    {
        var plugin = new VpnFleetPlugin(new FakeTimeProvider(), new FakeGitHub());

        Assert.Equal("Connect 0 devices to Tailscale", plugin.AuditDetail(Fleet.Connect, """{"kind":"Tailscale","deviceIds":null}"""));
        Assert.Equal("Change Tailscale settings: ", plugin.AuditDetail(Fleet.SaveSettings, """{"kind":"Tailscale","values":null}"""));
        Assert.Equal("Install Tailscale on 0 devices", plugin.AuditDetail(Fleet.Install, """{"kind":"Tailscale","deviceIds":null}"""));
    }

    [Fact]
    public async Task A_read_that_fails_never_stays_reading()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Throw = new InvalidOperationException("broken");
        server.Add(new FakeDevice(), new AppCamera());

        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, Json(new RefreshRequest(true, [])));

        await NextStateAsync(server, s => !s.Reading);
    }

    [Theory]
    [InlineData("10.0.0.10", "51820", "10.0.0.10:51820")]
    [InlineData("vpn.example.com:4500", "51820", "vpn.example.com:4500")]
    [InlineData("fe80::1", "51820", "[fe80::1]:51820")]
    [InlineData("[fe80::1]", "51820", "[fe80::1]:51820")]
    [InlineData("[fe80::1]:4500", "51820", "[fe80::1]:4500")]
    public void An_IPv6_hub_gets_its_brackets(string address, string port, string expected) =>
        Assert.Equal(expected, DeviceVpn.HubEndpoint(address, port));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, VpnState.Unknown)]
    [InlineData(HttpStatusCode.ServiceUnavailable, VpnState.NotConnected)]
    public async Task A_NetBird_error_page_never_asks_for_a_setup_key(HttpStatusCode code, VpnState state)
    {
        var camera = new AppCamera { Respond = (_, _) => new HttpResponseMessage(code) { Content = new StringContent("<html>error</html>") } };

        var status = await DeviceVpn.ReadStatusAsync(camera, VpnApps.NetBird, CancellationToken.None);

        Assert.Equal(state, status.State);
        Assert.Contains($"HTTP {(int)code}", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_app_missing_after_the_install_is_never_running_already()
    {
        var (plugin, _, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var camera = new AppCamera { UploadAnswer = "OK" }; // the device says OK but lists no app afterwards
        var ctx = new FakeTaskContext(camera) { Owner = plugin };

        var ex = await Assert.ThrowsAsync<VpnDeviceException>(() =>
            StepRun.RunAsync(ctx.Steps, () => plugin.InstallTask.ExecuteAsync(ctx, new FakeDevice(), Json(new InstallPayload(VpnKind.Tailscale, "1.104.7", false)), CancellationToken.None)));

        Assert.Equal("The device does not list Tailscale after the install. Check its apps and its system log.", ex.Message);
        Assert.Contains($"{InstallTaskPlugin.StartStep}: Failed", StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task A_failed_check_keeps_the_release_read_before()
    {
        var (_, _, github, time) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        using var catalog = new ReleaseCatalog(github, time, () => Path.GetTempPath());
        await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None);
        github.Releases.Clear();

        var (release, error) = await catalog.GetAsync(VpnKind.Tailscale, checkNow: true, CancellationToken.None);

        Assert.Equal("1.104.7", release?.Version);
        Assert.Equal("GitHub answered HTTP 404.", error);
        github.Release(VpnApps.Tailscale, "v1.104.8", TailscaleAssets);
        time.Advance(ReleaseCatalog.KeepFailureFor);
        Assert.Equal("1.104.8", (await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None)).Release?.Version); // asked again after a failure's time
    }

    [Fact]
    public async Task A_huge_GitHub_answer_is_not_read()
    {
        var (_, _, github, time) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        github.HugeAnswer = true;
        using var catalog = new ReleaseCatalog(github, time, () => Path.GetTempPath());

        var (release, error) = await catalog.GetAsync(VpnKind.Tailscale, false, CancellationToken.None);

        Assert.Null(release);
        Assert.Equal("GitHub's answer could not be read.", error);
    }

    [Fact]
    public async Task A_broken_download_leaves_no_part_file()
    {
        var (_, _, github, time) = await TestPlugin.StartAsync();
        github.Release(VpnApps.ZeroTier, "v1.16.17", "signed_ZeroTier_VPN_1_16_17_aarch64.eap");
        var folder = Path.Combine(Path.GetTempPath(), "vpnfleet-tests", Guid.NewGuid().ToString("N"));
        using var catalog = new ReleaseCatalog(github, time, () => folder);
        var asset = (await catalog.GetAsync(VpnKind.ZeroTier, false, CancellationToken.None)).Release!.Assets[0];
        github.BreakDownloads = true;

        await Assert.ThrowsAsync<IOException>(() => catalog.DownloadAsync(asset, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Theory]
    [InlineData("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/v1/a.eap", true)]
    [InlineData("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/../../../Other/releases/download/v1/a.eap", false)]
    [InlineData("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/%2e%2e/a.eap", false)]
    [InlineData("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/v1%2F..%2F..%2Fx/a.eap", false)]
    [InlineData("https://github.com/Mo3he/Axis_Cam_NetBird/releases/download/v1/a.eap", false)]
    [InlineData("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/v1/a.eap?x=1", false)]
    [InlineData("https://github.com:444/Mo3he/Axis_Cam_Tailscale/releases/download/v1/a.eap", false)]
    [InlineData("http://github.com/Mo3he/Axis_Cam_Tailscale/releases/download/v1/a.eap", false)]
    public void Only_the_apps_own_downloads_count(string url, bool own) =>
        Assert.Equal(own, ReleaseCatalog.IsOwnDownload(VpnApps.Tailscale, url, "a.eap"));

    [Fact]
    public async Task Without_its_data_folder_the_plugin_downloads_nothing()
    {
        var github = new FakeGitHub().Release(VpnApps.ZeroTier, "v1.16.17", "signed_ZeroTier_VPN_1_16_17_aarch64.eap");
        using var plugin = new VpnFleetPlugin(new FakeTimeProvider(), github);
        await plugin.StartAsync(new FakeCoreContext(), CancellationToken.None);
        var asset = (await plugin.Catalog.GetAsync(VpnKind.ZeroTier, false, CancellationToken.None)).Release!.Assets[0];

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.Catalog.DownloadAsync(asset, null, CancellationToken.None));

        Assert.Contains("no data folder", ex.Message, StringComparison.Ordinal);
    }

    // ---- Helpers ----

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Fleet.Json);

    private static ConnectPayload WireGuardPayload(Guid deviceId, Guid hubTask)
    {
        var keys = WireGuardKeys.Generate();
        return new ConnectPayload
        {
            Settings = new ConnectRequest { Kind = VpnKind.WireGuard, HubDeviceId = Guid.NewGuid() },
            Cameras = new Dictionary<Guid, WireGuardCamera> { [deviceId] = new(keys.PrivateKey, "10.66.0.3/24") { PublicKey = keys.PublicKey } },
            Endpoint = "vpn.example.com:51820",
            PeerPublicKey = WireGuardKeys.Generate().PublicKey,
            AllowedIps = "10.66.0.0/24",
            HubTaskId = hubTask,
        };
    }

    /// <summary>A camera set up behind the hub: its record, 10.66.0.3 in 10.66.0.0/24.</summary>
    private static (FakeDevice Device, AppCamera Camera) CameraBehindHub(FakeCoreContext server, Guid hubId, out string publicKey)
    {
        var keys = WireGuardKeys.Generate();
        publicKey = keys.PublicKey;
        var camera = new AppCamera().WithApp("wireguardconfig");
        camera.Parameters["wireguardconfig.PrivateKey"] = keys.PrivateKey;
        camera.Parameters["wireguardconfig.ClientIP"] = "10.66.0.3/24";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000001" }, camera);
        var records = server.PluginSettings.Values.TryGetValue(VpnFleetPlugin.WireGuardSettingKey, out var json) && json is not null
            ? JsonSerializer.Deserialize<List<WireGuardRecord>>(json, Fleet.Json)!
            : [];
        records.Add(new WireGuardRecord(device.Id, "ACCC8E000001", keys.PublicKey, "10.66.0.3", "10.66.0.0/24", hubId));
        server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey] = JsonSerializer.Serialize(records, Fleet.Json);
        return (device, camera);
    }

    private static List<WireGuardRecord> Records(FakeCoreContext server) =>
        JsonSerializer.Deserialize<List<WireGuardRecord>>(server.PluginSettings.Values[VpnFleetPlugin.WireGuardSettingKey]!, Fleet.Json)!;

    private static async Task<FakeTaskContext> RunInstallAsync(VpnFleetPlugin plugin, AppCamera camera, InstallPayload payload, FakeUploadedFiles? uploads = null)
    {
        var ctx = new FakeTaskContext(camera) { Owner = plugin, UploadedFiles = uploads };
        await StepRun.RunAsync(ctx.Steps, () => plugin.InstallTask.ExecuteAsync(ctx, new FakeDevice(), Json(payload), CancellationToken.None));
        return ctx;
    }

    private static async Task<SaveSettingsReply> SaveAsync(VpnFleetPlugin plugin, FakeCoreContext server, SaveSettingsRequest request) =>
        JsonSerializer.Deserialize<SaveSettingsReply>((await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.SaveSettings, Json(request)))!, Fleet.Json)!;

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
