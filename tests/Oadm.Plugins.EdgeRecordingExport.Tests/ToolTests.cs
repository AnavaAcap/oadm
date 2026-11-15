using System.Text.Json.Nodes;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Plugins.EdgeRecordingExport.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>The tool of Edge Recording Export (docs/spec/mcp.md "Plugins"): the page's search of one camera, summary first, paged, read only.</summary>
public sealed class ToolTests
{
    private static async Task<JsonNode> SearchAsync(Rig rig, JsonObject payload) =>
        JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Call, RecordingTools.Search.Method, payload.ToJsonString(), CancellationToken.None))!)!;

    [Fact]
    public async Task A_search_answers_the_summary_and_the_recordings_oldest_first_a_page_at_a_time()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(7, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        ICorePlugin core = rig.Plugin;
        Assert.Equal("recordings", core.ToolArea);
        Assert.Equal(["search", "export"], core.Tools.Select(t => t.Name));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(RecordingTools.Search.Method));
        Assert.False(core.IsAudited(RecordingTools.Search.Method));

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 5; page++)
        {
            var payload = new JsonObject { ["device_id"] = device.Id.ToString(), ["limit"] = 3 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = await SearchAsync(rig, payload);
            Assert.Null(answer["problem"]);
            Assert.Equal(7, answer["total"]!.GetValue<int>());
            Assert.Equal("mp4", answer["format"]!.GetValue<string>());
            Assert.True(answer["estimated_bytes"]!.GetValue<long>() > 0);
            Assert.Equal("2026-10-12T18:00:00Z", answer["first"]!.GetValue<string>());
            seen.AddRange(answer["recordings"]!.AsArray().Select(r => r!["start"]!.GetValue<string>()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(seen.Order(StringComparer.Ordinal), seen); // oldest first
        Assert.All(camera.Requests, r => Assert.DoesNotContain("remove", r, StringComparison.Ordinal));
        Assert.DoesNotContain(camera.Requests, r => r.StartsWith(EdgeStorageRequests.ExportPath, StringComparison.Ordinal)); // no export

        var ranged = await SearchAsync(rig, new JsonObject
        {
            ["device_id"] = device.Id.ToString(),
            ["start"] = FakeCamera.T0.AddSeconds(65).ToString("O"),
            ["stop"] = FakeCamera.T0.AddMinutes(3).AddSeconds(5).ToString("O"),
        });
        Assert.Equal(3, ranged["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task Wrong_input_is_a_field_error_and_a_camera_that_cannot_record_says_why()
    {
        var rig = new Rig();
        var device = rig.Add(FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)), "10.0.0.48");
        var encoder = rig.Add(null, "10.0.0.49", category: DeviceCategory.Speaker);

        Assert.Equal("device_id", Assert.Single((await SearchAsync(rig, []))["errors"]!.AsArray())!["field"]!.GetValue<string>());
        var reversed = await SearchAsync(rig, new JsonObject
        {
            ["device_id"] = device.Id.ToString(),
            ["start"] = FakeCamera.T0.AddMinutes(5).ToString("O"),
            ["stop"] = FakeCamera.T0.ToString("O"),
        });
        Assert.Equal("stop", Assert.Single(reversed["errors"]!.AsArray())!["field"]!.GetValue<string>());

        var speaker = await SearchAsync(rig, new JsonObject { ["device_id"] = encoder.Id.ToString() });
        Assert.Equal(EdgeRecordingTexts.NotVideo, speaker["problem"]!.GetValue<string>());
        Assert.Equal(0, speaker["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_export_tool_answers_the_pages_download_as_a_file_and_reads_nothing_from_the_camera()
    {
        var rig = new Rig();
        var camera = FakeCamera.WithClips(1, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var device = rig.Add(camera, "10.0.0.48");
        ICorePlugin core = rig.Plugin;
        var tool = RecordingTools.Export;
        Assert.True(tool.ReturnsFile);
        Assert.False(tool.Changes);
        Assert.Equal(EdgeRecordingInfo.ExportKey, tool.Permission);
        Assert.Equal(EdgeRecordingInfo.ExportKey, core.RequiredPermission(tool.Method));
        Assert.False(core.IsAudited(tool.Method)); // the fetch of the file is the audited export

        var recording = (await SearchAsync(rig, new JsonObject { ["device_id"] = device.Id.ToString() }))["recordings"]![0]!;
        var requestsBefore = camera.Requests.Count;
        var answer = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Call, tool.Method, new JsonObject
        {
            ["device_id"] = device.Id.ToString(),
            ["recording_id"] = recording["id"]!.GetValue<string>(),
            ["disk_id"] = recording["disk_id"]!.GetValue<string>(),
        }.ToJsonString(), CancellationToken.None))!)!;
        Assert.Equal(requestsBefore, camera.Requests.Count); // the tool only names the file
        var file = answer["file"]!;
        Assert.Equal(EdgeRecordingMethods.Export, file["method"]!.GetValue<string>());
        Assert.Equal("video/mp4", file["content_type"]!.GetValue<string>());
        Assert.EndsWith(".mp4", file["file_name"]!.GetValue<string>(), StringComparison.Ordinal);
        var request = System.Text.Json.JsonSerializer.Deserialize<ExportRequest>(file["payload_json"]!.GetValue<string>(), EdgeRecordingInfo.Json)!;
        Assert.Equal((device.Id, recording["id"]!.GetValue<string>(), "mp4"), (request.DeviceId, request.RecordingId, request.Format));

        // The payload is the page's own export: the host fetches it with the caller's rights.
        await using (var download = await rig.Plugin.OpenDownloadAsync(rig.Call, file["method"]!.GetValue<string>(), file["payload_json"]!.GetValue<string>(), CancellationToken.None))
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            Assert.True(copy.Length > 0);
        }

        var errors = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Call, tool.Method, "{}", CancellationToken.None))!)!["errors"]!.AsArray()
            .Select(e => e!["field"]!.GetValue<string>()).ToList();
        Assert.Equal(["device_id", "recording_id", "disk_id"], errors);
        var unknown = JsonNode.Parse((await rig.Plugin.InvokeAsync(rig.Call, tool.Method,
            $$"""{"device_id":"{{Guid.NewGuid()}}","recording_id":"r","disk_id":"SD_DISK"}""", CancellationToken.None))!)!;
        Assert.Equal("device_id", Assert.Single(unknown["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tool_reaches_no_camera_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        var plugin = new EdgeRecordingExportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(RecordingTools.Search.Method, """{"device_id":"{device}"}""")
            .Method(RecordingTools.Export.Method, """{"device_id":"{device}","recording_id":"r1","disk_id":"SD_DISK"}""")
            .RunAsync();
    }
}
