using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.HardeningScan.Scanning;
using Oadm.Plugins.HardeningScan.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Start scan, Stop scan and the detail, with the
/// rights of their page methods, on the caller's devices only.
/// </summary>
public sealed class PageToolTests
{
    private static string Ids(params Guid[] ids) => new JsonArray([.. ids.Select(i => (JsonNode)i.ToString())]).ToJsonString();

    [Fact]
    public void The_tools_have_the_rights_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new HardeningScanPlugin();
        var set = HardeningPageTools.Set;
        Assert.Equal(HardeningMethods.StartScan, set.PageMethodOf("tool.scan_start"));
        Assert.Equal("scan", core.RequiredPermission("tool.scan_start"));
        Assert.Equal("scan", core.RequiredPermission("tool.scan_stop"));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.detail"));
        Assert.All(set.Tools, t => Assert.False(t.Changes)); // the page starts and stops a scan without asking
        Assert.All(set.Tools, t => Assert.False(core.IsAudited(t.Method)));
        Assert.True(set.ToolOf("tool.scan_start")!.DeviceFilter);
    }

    [Fact]
    public async Task Start_scans_the_chosen_devices_and_the_detail_shows_their_checks()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var started = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.scan_start", $$"""{"level":"extended","device_ids":{{Ids(a.Id)}},"devices_total":1}"""))!)!;
        Assert.Equal("extended", started["level"]!.GetValue<string>());
        Assert.Equal(1, started["total"]!.GetValue<int>());
        await plugin.Service!.WaitAsync();

        var detail = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.detail", $$"""{"device_id":"{{a.Id}}"}"""))!)!;
        var level = Assert.Single(detail["levels"]!.AsArray())!;
        Assert.Equal("extended", level["level"]!.GetValue<string>());
        Assert.True(level["checks"]!.AsArray().Count > 0 || level["status"] is not null);

        // All devices: the page's "every device of the view" (an empty list).
        var all = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.scan_start", """{"all_devices":true}"""))!)!;
        Assert.Equal(2, all["total"]!.GetValue<int>());
        await plugin.Service!.WaitAsync();
    }

    [Fact]
    public async Task Invalid_input_is_refused_with_field_errors_and_starts_nothing()
    {
        var ctx = new Testing.FakeCoreContext();
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var none = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.scan_start", """{"device_ids":[],"devices_total":0}"""))!)!;
        Assert.Equal(HardeningPageTools.ChooseDevices, none["errors"]![0]!["error"]!.GetValue<string>());
        var deep = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.scan_start", """{"level":"deep","all_devices":true}"""))!)!;
        Assert.Equal("level", deep["errors"]![0]!["field"]!.GetValue<string>());
        var many = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.scan_start", $$"""{"device_ids":["{{Guid.NewGuid()}}"],"devices_total":501}"""))!)!;
        Assert.Equal("filter", many["errors"]![0]!["field"]!.GetValue<string>());
        var badId = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.detail", """{"device_id":"lobby"}"""))!)!;
        Assert.Equal("device_id", badId["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Null(plugin.Service!.CurrentJob);

        // Stop without a scan: the page's text.
        var stop = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, admin, "tool.scan_stop", null));
        Assert.Equal(HardeningScanService.NotYours, stop.Message);
    }

    [Fact]
    public async Task A_group_scans_and_reads_only_its_own_devices()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = ctx.CallAsGroup([inside.Id], "tech2");

        var all = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, "tool.scan_start", """{"all_devices":true}"""))!)!;
        Assert.Equal(1, all["total"]!.GetValue<int>());
        await plugin.Service!.WaitAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, group, "tool.detail", $$"""{"device_id":"{{outside.Id}}"}"""));
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, group, "tool.scan_start", $$"""{"device_ids":["{{outside.Id}}"],"devices_total":1}"""));

        var stopped = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, "tool.scan_stop", null))!)!;
        Assert.Equal("done", stopped["state"]!.GetValue<string>()); // the ended scan of the group's scope
    }
}
