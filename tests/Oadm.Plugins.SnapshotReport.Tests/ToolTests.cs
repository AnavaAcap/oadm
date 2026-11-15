using System.Text.Json.Nodes;

using Oadm.Plugins.SnapshotReport.Tools;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>The tool of the Snapshot report (docs/spec/mcp.md "Plugins"): the tiles of chosen cameras, at most 20, the caller's devices only, read only.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_tiles_of_the_chosen_cameras_carry_the_report_facts_and_at_most_20_cameras_go_in_one_call()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48", Model = "AXIS P3265-V" });
        var speaker = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.50", Category = Oadm.Sdk.Devices.DeviceCategory.Speaker });
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("sources", Assert.Single(core.Tools).Name);
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(SnapshotTools.Sources.Method));
        Assert.False(core.IsAudited(SnapshotTools.Sources.Method));
        var admin = ctx.CallAsAllDevices();

        var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.Sources.Method, $$"""{"device_ids":["{{camera.Id}}","{{speaker.Id}}"]}"""))!)!;
        var tile = answer["tiles"]!.AsArray().Single(t => t!["device_id"]!.GetValue<string>() == camera.Id.ToString())!;
        Assert.Equal(("10.0.0.48", "AXIS P3265-V", 1), (tile["address"]!.GetValue<string>(), tile["model"]!.GetValue<string>(), tile["camera"]!.GetValue<int>()));
        Assert.DoesNotContain(answer["tiles"]!.AsArray(), t => t!["device_id"]!.GetValue<string>() == speaker.Id.ToString() && t["error"] is null);
        Assert.Empty(ctx.PublishedEvents);

        foreach (var payload in new[] { "{}", """{"device_ids":[]}""", new JsonObject { ["device_ids"] = new JsonArray([.. Enumerable.Range(0, 21).Select(_ => (JsonNode)Guid.NewGuid().ToString())]) }.ToJsonString() })
        {
            var refused = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.Sources.Method, payload))!)!;
            Assert.Equal("device_ids", Assert.Single(refused["errors"]!.AsArray())!["field"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task The_tool_reaches_no_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(SnapshotTools.Sources.Method, """{"device_ids":["{inside}","{device}"]}""")
            .RunAsync();
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, $$"""{"device_ids":["{{camera.Id}}"]}"""));
        }

        Assert.Empty(ctx.PluginSettings.Values);
        Assert.Equal(0, ctx.PluginRecords.WrittenRecords);
        Assert.Empty(ctx.FakeJobs.List(admin)); // no report was started
        Assert.Equal(0, ctx.Cameras[camera.Id].Writes);
    }
}
