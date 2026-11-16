using System.Text.Json;

using Oadm.Plugins.VpnFleet.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.VpnFleet.Tests;

/// <summary>The wizards of Install or upgrade and Set up many cameras: steps, what blocks Next, the plan, the start.</summary>
public sealed class WizardTests
{
    private static readonly string[] TailscaleAssets = ["signed_Tailscale_VPN_1_104_7_aarch64.eap"];

    [Fact]
    public async Task Install_goes_from_the_app_to_the_cameras_to_the_plan_and_starts_one_task_each()
    {
        var (page, server, _) = await PageAsync();
        var wizard = page.InstallWizard();
        Assert.Equal("The newest version is not known.", wizard.NextBlockedReason);

        await wizard.Install!.LoadReleasesAsync(checkNow: false);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsCamerasStep);
        Assert.Equal(
            ["10.0.0.48 Upgrade True", "10.0.0.49 Install False", "10.0.0.50 UpToDate False", "10.0.0.51 NotRead False"],
            wizard.Cameras.All.Select(c => $"{c.Address} {c.Kind} {c.IsChecked}"));

        wizard.TickWithoutAppCommand.Execute(null);
        wizard.Cameras.All[0].IsChecked = true;
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsCheckStep);
        Assert.Equal(["Upgrade Tailscale to 1.104.7 on 1 camera. Their settings stay.", "Install Tailscale 1.104.7 on 1 camera."], wizard.Plan);
        Assert.Equal("Install or upgrade 2 cameras", wizard.NextText);

        string? closed = null;
        wizard.CloseRequested += (_, _) => closed = wizard.Result;
        await wizard.NextCommand.ExecuteAsync(null);

        var run = Assert.Single(server.TaskRuns);
        Assert.Equal(InstallTaskPlugin.PluginId, run.PluginId);
        Assert.Equal(2, run.DeviceIds.Count);
        Assert.False(JsonSerializer.Deserialize<InstallPayload>(run.PayloadJson!, Fleet.Json)!.Upgrade); // one camera has no app yet
        Assert.StartsWith("Installing Tailscale 1.104.7 on 2 cameras.", closed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Back_keeps_the_ticks_and_a_new_app_lists_the_cameras_again()
    {
        var (page, _, _) = await PageAsync();
        var wizard = page.InstallWizard();
        await wizard.Install!.LoadReleasesAsync(checkNow: false);
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Cameras.CheckAllCommand.Execute(null);

        wizard.BackCommand.Execute(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(4, wizard.Cameras.CheckedCount); // same app: the ticks stay

        wizard.BackCommand.Execute(null);
        wizard.Install.SelectedApp = wizard.Install.Apps.Single(a => a.Kind == VpnKind.ZeroTier);
        Assert.Equal("The newest version is not known.", wizard.NextBlockedReason); // no ZeroTier release on the fake GitHub
    }

    [Fact]
    public async Task Nothing_ticked_blocks_the_plan()
    {
        var (page, _, _) = await PageAsync();
        var wizard = page.InstallWizard();
        await wizard.Install!.LoadReleasesAsync(checkNow: false);
        await wizard.NextCommand.ExecuteAsync(null);

        wizard.Cameras.CheckNoneCommand.Execute(null);

        Assert.Equal("Tick the cameras first.", wizard.NextBlockedReason);
        Assert.False(wizard.Steps[RolloutWizardViewModel.CheckStep].CanOpen);
    }

    [Fact]
    public async Task Set_up_needs_its_key_warns_about_cameras_without_the_app_and_forgets_the_key()
    {
        var (page, server, _) = await PageAsync();
        var wizard = page.SetupWizard();
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsAppStep);
        Assert.Equal("Enter an auth key.", wizard.NextBlockedReason);

        wizard.Form!.AuthKey = "tskey-auth-k3y";
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(
            ["10.0.0.48 Ready True", "10.0.0.49 NoApp False", "10.0.0.50 Ready True", "10.0.0.51 NotRead True"],
            wizard.Cameras.All.Select(c => $"{c.Address} {c.Kind} {c.IsChecked}"));

        wizard.Cameras.CheckAllCommand.Execute(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(
            ["Set up Tailscale on 2 cameras: save the settings, start the app and wait for the tunnel.",
             "Set up Tailscale on 1 camera not read yet. A camera without the app is not changed."],
            wizard.Plan);
        Assert.Equal("Ticked without the app or not readable: 1 camera. These tasks fail and change nothing.", wizard.PlanWarning);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(4, Assert.Single(server.TaskRuns).DeviceIds.Count);
        Assert.Equal("", wizard.Form.AuthKey); // sent, then forgotten
        Assert.NotNull(wizard.Result);
    }

    [Fact]
    public async Task A_refused_start_stays_open_with_the_reason()
    {
        var (page, server, _) = await PageAsync();
        foreach (var camera in server.Cameras.Values)
        {
            camera.Parameters["wireguardconfig.ClientIP"] = ""; // the WireGuard app: only such cameras get an address
        }

        var wizard = page.SetupWizard();
        wizard.Form!.SelectedVpn = wizard.Form.Vpns.Single(v => v.Kind == VpnKind.WireGuard);
        wizard.Form.UseHub = false;
        wizard.Form.Endpoint = "vpn.example.com:51820";
        wizard.Form.ServerPublicKey = WireGuardKeys.Generate().PublicKey;
        wizard.Form.AddressPool = "10.9.0.1/30"; // room for one camera only
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Cameras.CheckAllCommand.Execute(null);
        await wizard.NextCommand.ExecuteAsync(null);

        var closed = false;
        wizard.CloseRequested += (_, _) => closed = true;
        await wizard.NextCommand.ExecuteAsync(null);

        Assert.False(closed);
        Assert.Contains("no free address", wizard.ErrorText, StringComparison.Ordinal);
        Assert.Empty(server.TaskRuns);
        Assert.Null(wizard.Result);
    }

    [Fact]
    public async Task The_hub_is_never_a_camera_of_the_set_up()
    {
        var (page, _, devices) = await PageAsync();
        var wizard = page.SetupWizard();
        wizard.Form!.SelectedVpn = wizard.Form.Vpns.Single(v => v.Kind == VpnKind.WireGuard);
        wizard.Form.HubDeviceId = devices[2].Id;
        await wizard.NextCommand.ExecuteAsync(null);

        var hub = wizard.Cameras.All.Single(c => c.Id == devices[2].Id);
        Assert.Equal((CheckKind.Hub, false, false), (hub.Kind, hub.IsChecked, hub.IsEnabled));
        wizard.Cameras.CheckAllCommand.Execute(null);
        Assert.False(hub.IsChecked);
    }

    /// <summary>A read page of four cameras: Tailscale 1.104.6, none, Tailscale 1.104.7, one not read.</summary>
    private static async Task<(VpnFleetViewModel Page, FakeCoreContext Server, IReadOnlyList<FakeDevice> Devices)> PageAsync()
    {
        var (plugin, server, github, _) = await TestPlugin.StartAsync();
        github.Release(VpnApps.Tailscale, "v1.104.7", TailscaleAssets);
        var devices = new[]
        {
            server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN", version: "1.104.6")),
            server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera()),
            server.Add(new FakeDevice { Address = "10.0.0.50" }, new AppCamera().WithApp("Tailscale_VPN", version: "1.104.7")),
            server.Add(new FakeDevice { Address = "10.0.0.51" }, new AppCamera()),
        };
        var page = new VpnFleetViewModel(new FakePageContext(plugin, server) { SelectedDevices = devices });
        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh,
            JsonSerializer.Serialize(new RefreshRequest(false, [.. devices.Take(3).Select(d => d.Id)]), Fleet.Json));
        page.Apply(JsonSerializer.Serialize(await FleetEvents.NextStateAsync(server, s => !s.Reading && s.Rows.Count > 0), Fleet.Json));

        return (page, server, devices);
    }
}
