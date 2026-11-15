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
        Assert.Equal(["sources", "report", "report_file"], core.Tools.Select(t => t.Name));
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
    public async Task A_report_is_previewed_without_a_camera_then_started_and_its_pdf_is_a_file_of_its_own_user()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48", Model = "AXIS P3265-V" });
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.True(SnapshotTools.Report.Changes);
        Assert.True(SnapshotTools.ReportFile.ReturnsFile);
        Assert.Equal("report", core.RequiredPermission(SnapshotTools.Report.PreviewMethod));
        Assert.Equal("report", core.RequiredPermission(SnapshotTools.ReportFile.Method));
        var admin = ctx.CallAsAllDevices();
        var input = $$"""{"site":"Lobby","technician":"Ann","date":"2026-11-15","items":[{"device_id":"{{camera.Id}}"}]}""";

        var plan = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.Report.PreviewMethod, input))!)!;
        Assert.Equal((1, 1), (plan["pictures"]!.GetValue<int>(), plan["cameras"]!.GetValue<int>()));
        Assert.Empty(ctx.Cameras[camera.Id].Requests); // the preview takes no picture
        Assert.Empty(ctx.FakeJobs.List(admin));

        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.Report.PreviewMethod, """{"date":"15.11.2026"}"""))!)!;
        Assert.Equal(["site", "technician", "date", "items"], bad["errors"]!.AsArray().Select(e => e!["field"]!.GetValue<string>()));

        var started = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.Report.RunMethod, input))!)!;
        var jobId = started["job_id"]!.GetValue<string>();
        JsonNode state = started;
        await Testing.Eventually.TrueAsync(async () =>
        {
            state = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, SnapshotTools.ReportFile.Method, $$"""{"job_id":"{{jobId}}"}"""))!)!;
            return state["state"]!.GetValue<string>() != "running";
        }, "the report ends");
        Assert.Equal("done", state["state"]!.GetValue<string>());
        var file = state["file"]!;
        Assert.Equal((SnapshotReportMethods.DownloadReport, "application/pdf"), (file["method"]!.GetValue<string>(), file["content_type"]!.GetValue<string>()));

        await using (var download = await plugin.OpenDownloadAsync(admin, file["method"]!.GetValue<string>(), file["payload_json"]!.GetValue<string>(), CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(copy.ToArray(), 0, 4));
        }

        // Another user's job answers like an unknown one, for the tool and for the file.
        var other = ctx.CallAsAllDevices("someone else");
        var foreign = JsonNode.Parse((await ctx.InvokeAsync(plugin, other, SnapshotTools.ReportFile.Method, $$"""{"job_id":"{{jobId}}"}"""))!)!;
        Assert.Equal("job_id", Assert.Single(foreign["errors"]!.AsArray())!["field"]!.GetValue<string>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.OpenDownloadAsync(other, SnapshotReportMethods.DownloadReport, file["payload_json"]!.GetValue<string>(), CancellationToken.None));
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
        foreach (var tool in core.Tools.Where(t => !t.Changes))
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, $$"""{"device_ids":["{{camera.Id}}"]}"""));
        }

        Assert.Empty(ctx.PluginSettings.Values);
        Assert.Equal(0, ctx.PluginRecords.WrittenRecords);
        Assert.Empty(ctx.FakeJobs.List(admin)); // no report was started
        Assert.Equal(0, ctx.Cameras[camera.Id].Writes);
    }
}
