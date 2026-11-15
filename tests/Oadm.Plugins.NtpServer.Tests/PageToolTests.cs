using System.Text.Json.Nodes;

using Oadm.Plugins.NtpServer.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>
/// The page's Save as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): a preview with the page's checks and the warnings
/// that changes nothing, a run through the page's save, the rights of the page's Save (Administrator, "manage", all devices).
/// The server listens on the loopback on a random port, never on port 123.
/// </summary>
public sealed class PageToolTests
{
    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    [Fact]
    public void Settings_save_has_the_rights_of_the_page_save()
    {
        ICorePlugin core = new NtpServerPlugin();
        Assert.Equal(NtpServerMethods.Save, NtpPageTools.Set.PageMethodOf("tool.settings_save"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.settings_save"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.settings_save.preview"));
        Assert.Equal(core.RequiredPermission(NtpServerMethods.Save), core.RequiredPermission("tool.settings_save"));
        Assert.True(core.RequiresAllDevices("tool.settings_save"));
        Assert.True(core.RequiresAllDevices("tool.settings_save.preview"));
        Assert.True(core.IsAudited("tool.settings_save"));
        Assert.False(core.IsAudited("tool.settings_save.preview"));
        Assert.True(core.IsAudited(NtpServerMethods.Save));
    }

    [Fact]
    public async Task Settings_save_previews_without_a_change_and_the_run_turns_the_server_on_and_off()
    {
        var ctx = new Testing.FakeCoreContext();
        await using var plugin = new NtpServerPlugin(Options.Test());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var plan = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"enabled":true,"interface":"lo-id"}"""));
        Assert.Equal(("turn_on", NtpPageTools.ServerClock, "NTP server"), (plan["change"]!.GetValue<string>(), plan["time_source"]!.GetValue<string>(), plan["unit"]!.GetValue<string>()));
        Assert.Equal([NtpPageTools.ServesEveryone], plan["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.False(plugin.Service!.Config.Enabled);

        var done = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save", """{"enabled":true,"interface":"lo-id"}"""));
        Assert.True(done["enabled"]!.GetValue<bool>());
        Assert.Equal((true, "lo-id"), (plugin.Service.Config.Enabled, plugin.Service.Config.InterfaceId));

        // A change keeps the interface it leaves out; turning off says what the devices lose.
        var off = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"enabled":false}"""));
        Assert.Equal([NtpPageTools.DevicesLoseTime], off["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        await ctx.InvokeAsync(plugin, admin, "tool.settings_save", """{"enabled":false}""");
        Assert.Equal((false, "lo-id"), (plugin.Service.Config.Enabled, plugin.Service.Config.InterfaceId));
    }

    [Fact]
    public async Task Invalid_input_gets_the_page_texts_under_the_fields_and_a_group_user_is_refused()
    {
        var ctx = new Testing.FakeCoreContext();
        await using var plugin = new NtpServerPlugin(Options.Test());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var nic = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save.preview", """{"enabled":true,"interface":"Nowhere"}"""));
        Assert.Equal(("interface", NtpServerService.InterfaceGone), (nic["errors"]![0]!["field"]!.GetValue<string>(), nic["errors"]![0]!["error"]!.GetValue<string>()));
        var port = Answer(await ctx.InvokeAsync(plugin, admin, "tool.settings_save", """{"enabled":true,"interface":"lo-id","upstream":"pool.ntp.org:99999"}"""));
        Assert.Equal(("upstream", "The port must be a number from 1 to 65535."), (port["errors"]![0]!["field"]!.GetValue<string>(), port["errors"]![0]!["error"]!.GetValue<string>()));
        Assert.False(plugin.Service!.Config.Enabled);

        var device = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var group = ctx.CallAsGroup([device.Id], "guard");
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, group, "tool.settings_save.preview", """{"enabled":true}"""));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, group, "tool.settings_save", """{"enabled":true}"""));
        Assert.False(plugin.Service.Config.Enabled);
    }
}
