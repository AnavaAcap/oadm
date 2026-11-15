using System.Text.Json.Nodes;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Tests.Support;
using Oadm.Plugins.Radius.Tools;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>The page's Sessions tab and Delete log as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15), with the rights of their page methods.</summary>
public sealed class PageToolTests
{
    [Fact]
    public void The_tools_have_the_rights_of_their_page_methods()
    {
        ICorePlugin core = new RadiusPlugin();
        Assert.Equal(core.RequiredRole(RadiusMethods.ListSessions), core.RequiredRole("tool.sessions"));
        Assert.Equal(UserRole.Operator, core.RequiredRole("tool.sessions"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.log_delete"));
        Assert.Equal(UserRole.Admin, core.RequiredRole("tool.log_delete.preview"));
        Assert.True(core.RequiresAllDevices("tool.log_delete"));
        Assert.True(core.RequiresAllDevices("tool.log_delete.preview"));
        Assert.False(core.RequiresAllDevices("tool.sessions"));
        Assert.True(core.IsAudited("tool.log_delete"));
        Assert.False(core.IsAudited("tool.log_delete.preview"));
        Assert.False(core.IsAudited("tool.sessions"));
        Assert.True(RadiusPageTools.Set.ToolOf("tool.log_delete")!.Destructive);
    }

    [Fact]
    public async Task Sessions_show_the_callers_devices_and_Delete_log_previews_then_runs_once()
    {
        await using var harness = await PluginHarness.CreateAsync(start: false);
        var inside = harness.AddDevice("ACCC8E000001");
        var outside = harness.AddDevice("ACCC8E000002");
        await harness.StartAsync();
        await harness.AddSwitchAsync("Core switch", "10.0.0.2");
        foreach (var certificate in new[] { harness.Ca.Camera(inside.Serial), harness.Ca.Camera(outside.Serial) })
        {
            await harness.Switch().RunAsync(new TestSupplicant(new SupplicantOptions { Certificate = certificate, TrustedCa = harness.Ca.CaCertificate }));
        }

        harness.Tick();
        async Task<JsonNode> CallAsync(ICallContext call, string method, string? payload = null) =>
            JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, call, method, payload))!)!;

        var all = await CallAsync(harness.Admin, "tool.sessions");
        Assert.True(all["running"]!.GetValue<bool>());
        Assert.Equal(2, all["total"]!.GetValue<int>());
        var first = await CallAsync(harness.Admin, "tool.sessions", """{"limit":1}""");
        Assert.Single(first["sessions"]!.AsArray());
        var second = await CallAsync(harness.Admin, "tool.sessions", new JsonObject { ["limit"] = 1, ["after"] = first["next"]!.GetValue<string>() }.ToJsonString());
        Assert.Null(second["next"]);
        var mine = await CallAsync(harness.Ctx.CallAsGroup([inside.Id]), "tool.sessions");
        Assert.Equal(inside.Id.ToString(), Assert.Single(mine["sessions"]!.AsArray())!["device_id"]!.GetValue<string>());

        var entries = harness.Plugin.Service!.Log.Count;
        Assert.True(entries >= 2);
        var plan = await CallAsync(harness.Admin, "tool.log_delete.preview");
        Assert.Equal(entries, plan["will_change"]!.GetValue<int>());
        Assert.Equal("log line", plan["unit"]!.GetValue<string>());
        Assert.Equal(entries, harness.Plugin.Service!.Log.Count); // the preview deletes nothing

        var done = await CallAsync(harness.Admin, "tool.log_delete");
        Assert.Equal(entries, done["deleted"]!.GetValue<int>());
        Assert.Equal(0, harness.Plugin.Service!.Log.Count);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.log_delete", null));
        Assert.Equal(RadiusPageTools.EmptyLog, again.Message);
    }

    [Fact]
    public async Task While_the_server_is_off_there_are_no_sessions_and_nothing_to_delete()
    {
        await using var harness = await PluginHarness.CreateAsync(h => h.TurnedOn = false);
        var answer = JsonNode.Parse((await harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.sessions", null))!)!;
        Assert.False(answer["running"]!.GetValue<bool>());
        Assert.Empty(answer["sessions"]!.AsArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Ctx.InvokeAsync(harness.Plugin, harness.Admin, "tool.log_delete.preview", null));
    }
}
