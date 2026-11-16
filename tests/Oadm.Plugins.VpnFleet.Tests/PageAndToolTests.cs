using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Headless;

using Oadm.Plugins.VpnFleet.Client;
using Oadm.Plugins.VpnFleet.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.VpnFleet.Tests;

/// <summary>The page's actions as tools: the page's checks and rights, previews that never write.</summary>
public sealed class ToolTests
{
    private static readonly string[] TailscaleAssets = ["signed_Tailscale_VPN_1_104_7_aarch64.eap"];

    [Fact]
    public async Task Results_list_the_last_read_by_page_with_filters()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        for (var i = 0; i < 5; i++)
        {
            server.Add(new FakeDevice { Address = $"10.0.0.{10 + i}" }, new AppCamera().WithApp("Tailscale_VPN", version: i < 2 ? "1.104.6" : "1.104.7"));
        }

        Assert.Contains("Nothing read yet", (await ToolAsync(server, plugin, "results", "{}"))["note"]!.GetValue<string>(), StringComparison.Ordinal);
        await ToolAsync(server, plugin, "read", """{"all_devices":true}""");
        await Eventually.TrueAsync(() => !plugin.StateFor(server.CallAsAllDevices()).Reading && plugin.StateFor(server.CallAsAllDevices()).Rows.Count == 5, "the read ends");

        var first = await ToolAsync(server, plugin, "results", """{"limit":3}""");
        Assert.Equal(5, first["total"]!.GetValue<int>());
        Assert.Equal(2, first["updates"]!.GetValue<int>());
        Assert.Equal(3, first["rows"]!.AsArray().Count);
        var rest = await ToolAsync(server, plugin, "results", $$"""{"limit":3,"after":"{{first["next_cursor"]!.GetValue<string>()}}"}""");
        Assert.Equal(2, rest["rows"]!.AsArray().Count);
        Assert.Null(rest["next_cursor"]);

        var updates = await ToolAsync(server, plugin, "results", """{"updates_only":true}""");
        Assert.All(updates["rows"]!.AsArray(), r => Assert.Equal("1.104.7", r!["update"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Settings_never_answer_a_secret_and_a_save_preview_writes_nothing()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera().WithApp("Tailscale_VPN");
        camera.Parameters["Tailscale_VPN.AuthKey"] = "tskey-auth-k3y";
        camera.Parameters["Tailscale_VPN.AcceptDNS"] = "false";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);

        var settings = await ToolAsync(server, plugin, "settings", $$"""{"device_id":"{{device.Id}}","app":"tailscale"}""");
        Assert.DoesNotContain("tskey", settings.ToJsonString(), StringComparison.Ordinal);
        Assert.True(settings["settings"]![0]!["has_value"]!.GetValue<bool>());

        var refused = await ToolAsync(server, plugin, "settings_save", $$$"""{"device_id":"{{{device.Id}}}","app":"tailscale","values":{"AuthKey":"new"}}""", preview: true);
        Assert.Contains("change secrets on the VPN Fleet page", refused.ToJsonString(), StringComparison.Ordinal);

        var payload = $$$"""{"device_id":"{{{device.Id}}}","app":"tailscale","values":{"AcceptDNS":"true"}}""";
        var plan = await ToolAsync(server, plugin, "settings_save", payload, preview: true);
        Assert.Equal((1, "AcceptDNS = true"), (plan["will_change"]!.GetValue<int>(), plan["changes"]![0]!.GetValue<string>()));
        Assert.Equal(0, camera.Writes);

        var run = await ToolAsync(server, plugin, "settings_save", payload);
        Assert.Equal(1, run["changed"]!.GetValue<int>());
        Assert.Equal("true", camera.Parameters["root.Tailscale_VPN.AcceptDNS"]);
    }

    [Fact]
    public async Task Install_previews_the_devices_and_runs_one_task_each()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var a = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera());
        var b = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());
        var payload = $$"""{"app":"tailscale","device_ids":["{{a.Id}}","{{b.Id}}"],"devices_total":2}""";

        var plan = await ToolAsync(server, plugin, "install", payload, preview: true);
        Assert.Equal((2, "device", "1.104.7"), (plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>(), plan["version"]!.GetValue<string>()));
        Assert.Empty(server.TaskRuns);

        await ToolAsync(server, plugin, "install", payload);
        Assert.Equal([a.Id, b.Id], Assert.Single(server.TaskRuns).DeviceIds);
        Assert.Contains("filter", (await ToolAsync(server, plugin, "install", """{"app":"tailscale"}""", preview: true)).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_have_the_rights_of_their_page_methods_and_previews_are_not_audited()
    {
        var (plugin, _, _, _) = await TestPlugin.StartAsync();
        var install = VpnFleetTools.Set.ToolOf("tool.install")!;

        Assert.Equal(Fleet.InstallPermission, plugin.RequiredPermission(install.Method));
        Assert.Equal(Fleet.InstallPermission, plugin.RequiredPermission(install.PreviewMethod));
        Assert.Equal(Fleet.ConnectPermission, plugin.RequiredPermission("tool.settings_save"));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission("tool.results"));
        Assert.True(plugin.IsAudited(install.Method));
        Assert.False(plugin.IsAudited(install.PreviewMethod));
    }

    /// <summary>The canary for the tools that take device ids.</summary>
    [Fact]
    public async Task No_tool_reaches_a_device_outside_the_users_group()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var inside = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN"));
        var outside = server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera().WithApp("Tailscale_VPN"));

        await new PluginCanary(server, plugin, inside, outside)
            .Method("tool.read", """{"device_ids":["{inside}","{device}"],"devices_total":2}""")
            .Method("tool.settings", """{"device_id":"{device}","app":"tailscale"}""")
            .Method("tool.settings_save.preview", """{"device_id":"{device}","app":"tailscale","values":{"AcceptDNS":"true"}}""")
            .Method("tool.settings_save", """{"device_id":"{device}","app":"tailscale","values":{"AcceptDNS":"true"}}""")
            .Method("tool.install.preview", """{"app":"tailscale","device_ids":["{inside}","{device}"],"devices_total":2}""")
            .Method("tool.install", """{"app":"tailscale","device_ids":["{inside}","{device}"],"devices_total":2}""")
            .WithoutDevices("tool.results", "the table of the user's own group")
            .WithoutDevices("tool.releases", "the newest releases on GitHub")
            .RunAsync();
    }

    private static async Task<JsonNode> ToolAsync(FakeCoreContext server, VpnFleetPlugin plugin, string name, string payload, bool preview = false)
    {
        var tool = VpnFleetTools.Set.Tools.Single(t => t.Name == name);
        var answer = await server.InvokeAsync(plugin, server.CallAsAllDevices(), preview ? tool.PreviewMethod : tool.Method, payload);
        return JsonNode.Parse(answer!)!;
    }
}

/// <summary>The page offscreen like the client shell (OADM_SCREENSHOT_DIR writes PNGs), and the page at 100,000 devices.</summary>
public sealed class PageTests
{
    [Fact]
    public async Task Page_renders_the_table_and_its_panels()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", ["signed_Tailscale_VPN_1_104_7_aarch64.eap"]);
        var camera = new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6");
        camera.Parameters["Tailscale_VPN.AuthKey"] = "tskey-auth-x";
        camera.Parameters["Tailscale_VPN.CustomServer"] = "";
        camera.Parameters["Tailscale_VPN.AcceptDNS"] = "true";
        camera.Parameters["Tailscale_VPN.AcceptRoutes"] = "false";
        camera.Parameters["Tailscale_VPN.HttpProxyPort"] = "8080";
        var a = server.Add(new FakeDevice { Address = "10.0.0.48", Model = "P3265-V" }, camera);
        var b = server.Add(new FakeDevice { Address = "10.0.0.49", Model = "M3086-V" }, new AppCamera());
        var c = server.Add(new FakeDevice { Address = "10.0.0.50", Model = "Q6135-LE" }, new AppCamera());
        var d = server.Add(new FakeDevice { Address = "10.0.0.51", Model = "P1468-LE" }, new AppCamera());
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [.. server.DeviceList] };
        var state = new FleetState(5, false,
        [
            new FleetRow(a.Id, "10.0.0.48", "P3265-V", VpnKind.Tailscale, "1.104.6", VpnState.Connected, "100.64.0.9", "axis-b8a44f, example.org", null) { LatestVersion = "1.104.7" },
            new FleetRow(b.Id, "10.0.0.49", "M3086-V", VpnKind.NetBird, "0.83.1", VpnState.Connecting, "100.92.1.7", "signal down", null) { LatestVersion = "0.83.1" },
            new FleetRow(c.Id, "10.0.0.50", "Q6135-LE", null, null, VpnState.NoVpnApp, null, null, null),
            new FleetRow(d.Id, "10.0.0.51", "P1468-LE", null, null, VpnState.Unknown, null, null, DeviceMessages.CredentialsRequired),
        ]);

        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var shots = await session.Dispatch(async () =>
        {
            using var vm = new VpnFleetViewModel(ctx);
            var view = new VpnFleetView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm;
            vm.Apply(JsonSerializer.Serialize(state, Fleet.Json));
            Headless.Pump();
            Assert.Equal("4 devices: 1 connected, 1 not connected, 1 without a VPN app, 1 not read. 1 update", vm.Summary);
            Headless.Capture(window, "vpn-fleet.png");

            vm.SelectedRow = vm.Rows[0];
            await vm.Camera!.LoadAsync();
            Headless.Pump();
            Assert.Equal(["AuthKey", "CustomServer", "AcceptDNS", "AcceptRoutes", "HttpProxyPort"], vm.Camera.Fields.Select(f => f.Name));
            Headless.Capture(window, "vpn-fleet-camera.png");

            var install = vm.InstallWizard();
            await install.Install!.LoadReleasesAsync(checkNow: false);
            var installWindow = new RolloutWizardWindow();
            installWindow.Attach(install);
            installWindow.Show();
            install.Install.FromFiles = true;
            Headless.Pump();
            Headless.Capture(installWindow, "vpn-fleet-install-files.png");
            install.Install.FromFiles = false;
            await install.NextCommand.ExecuteAsync(null);
            Headless.Pump();
            Assert.True(install.IsCamerasStep);
            Assert.Equal("Upgrade 1.104.6 to 1.104.7", install.Cameras.Items[0].Note);
            Headless.Capture(installWindow, "vpn-fleet-install.png");
            await install.NextCommand.ExecuteAsync(null);
            Headless.Pump();
            Assert.Equal(["Upgrade Tailscale to 1.104.7 on 1 camera. Their settings stay."], install.Plan);
            Headless.Capture(installWindow, "vpn-fleet-install-check.png");
            installWindow.Close();

            var setup = vm.SetupWizard();
            setup.Form!.AuthKey = "tskey-auth-example";
            var setupWindow = new RolloutWizardWindow();
            setupWindow.Attach(setup);
            setupWindow.Show();
            Headless.Pump();
            Headless.Capture(setupWindow, "vpn-fleet-setup-vpn.png");
            setup.Form.SelectedVpn = setup.Form.Vpns.Single(v => v.Kind == VpnKind.WireGuard);
            Headless.Pump();
            Headless.Capture(setupWindow, "vpn-fleet-setup-wireguard.png");
            setup.Form.SelectedVpn = setup.Form.Vpns[0];
            await setup.NextCommand.ExecuteAsync(null);
            Headless.Pump();
            Assert.Equal("1 of 4 cameras ticked.", setup.Cameras.Summary);
            Headless.Capture(setupWindow, "vpn-fleet-setup.png");
            setupWindow.Close();
            window.Close();
            return 6;
        }, CancellationToken.None);
        Assert.Equal(6, shots);
    }

    [Fact]
    public Task A_hundred_thousand_devices_show_and_list() => TheMostDevicesAsync();

    [Fact]
    [Trait("Category", "Perf")]
    public async Task A_hundred_thousand_devices_show_and_list_fast()
    {
        var elapsed = await TheMostDevicesAsync();
        Assert.True(elapsed < 20_000, $"apply + both checklists took {elapsed} ms");
    }

    private static async Task<long> TheMostDevicesAsync()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        const int Devices = DeviceLimits.MaxDevices;
        var devices = Enumerable.Range(0, Devices).Select(i => new FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" }).ToList();
        server.DeviceList.AddRange(devices);
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [.. devices] };
        using var vm = new VpnFleetViewModel(ctx);
        var rows = devices.Select((dev, i) => new FleetRow(dev.Id, dev.Address, "P3265-V", VpnKind.Tailscale, i % 3 == 0 ? "1.104.6" : "1.104.7", VpnState.Connected, "100.64.0.1", null, null) { LatestVersion = "1.104.7" }).ToList();
        var json = JsonSerializer.Serialize(new FleetState(1, false, rows), Fleet.Json);

        var watch = Stopwatch.StartNew();
        vm.Apply(json);
        var install = vm.InstallChecklist(VpnKind.Tailscale, "1.104.7");
        var connect = vm.ConnectChecklist(VpnKind.Tailscale, null);
        var elapsed = watch.ElapsedMilliseconds;

        Assert.Equal(Devices, vm.Rows.Count);
        Assert.Equal((Devices + 2) / 3, install.Count(i => i.IsChecked));
        Assert.Equal(Devices, connect.Count(i => i.IsChecked));

        // One camera changes: its row in place, no reset of 100,000 rows, the selection stays.
        vm.SelectedRow = vm.Rows[50_000];
        var selected = vm.SelectedRow;
        var resets = TableEventTests.CountResets(vm);
        var changed = rows[42] with { State = VpnState.NotConnected };
        var update = Stopwatch.StartNew();
        vm.ApplyBatch([new FleetRowsEvent(2, [changed.DeviceId], [changed])], []);
        var single = update.ElapsedMilliseconds;
        Assert.Equal(0, resets());
        Assert.Same(selected, vm.SelectedRow);
        Assert.Equal("Not connected", vm.Table.TryGetRows(changed.DeviceId, out var views) ? views[0].StateText : null);
        Assert.StartsWith($"{Devices:N0} devices: {Devices - 1:N0} connected, 1 not connected", vm.Summary, StringComparison.Ordinal);
        Console.WriteLine($"{Devices:N0} rows: apply + both checklists {elapsed} ms, one row changed {single} ms");
        return elapsed;
    }
}
