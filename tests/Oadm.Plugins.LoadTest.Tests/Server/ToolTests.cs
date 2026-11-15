using System.Text.Json.Nodes;

using Oadm.Plugins.LoadTest.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The tools of the Load test (docs/spec/mcp.md "Plugins"): the checks of the caller's scope and one check's streams, paged, read only.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_runs_and_one_run_with_its_steps_and_streams_worst_first_a_page_at_a_time()
    {
        await using var rig = await new Rig(phaseSeconds: 3).StartAsync();
        var cameras = rig.Cameras(5);
        foreach (var camera in cameras.Take(2))
        {
            rig.Probes.Configure(camera.Id, Scripts.PacketLoss(5));
        }

        ICorePlugin core = rig.Plugin;
        Assert.Equal(["runs", "run"], core.Tools.Select(t => t.Name));
        Assert.All(core.Tools, t => Assert.Equal(PermissionKeys.Show, core.RequiredPermission(t.Method)));
        Assert.Empty(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestTools.Runs.Method, null))["runs"]!.AsArray());

        var started = await rig.StartCheckAsync(cameras, batchSize: 5);
        await rig.RunToEndAsync();

        var runs = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestTools.Runs.Method, null));
        Assert.Null(runs["running_id"]);
        var summary = Assert.Single(runs["runs"]!.AsArray())!;
        Assert.Equal(started.RunId.ToString(), summary["run_id"]!.GetValue<string>());
        Assert.Equal("completed", summary["state"]!.GetValue<string>());
        Assert.False(summary["passed"]!.GetValue<bool>());
        Assert.Equal(2, summary["failed_streams"]!.GetValue<int>());
        Assert.Equal("admin", summary["started_by"]!.GetValue<string>());

        var seen = new List<JsonNode>();
        string? after = null;
        for (var page = 0; page < 5; page++)
        {
            var payload = new JsonObject { ["run_id"] = started.RunId.ToString(), ["limit"] = 2 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestTools.Run.Method, payload.ToJsonString()));
            Assert.Equal(5, answer["total"]!.GetValue<int>());
            Assert.Equal(page == 0 ? 5 : 0, answer["steps"]!.AsArray().Count); // the steps come with the first page
            seen.AddRange(answer["streams"]!.AsArray().Select(s => s!));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(cameras.Select(c => c.Id.ToString()).Order(), seen.Select(s => s["device_id"]!.GetValue<string>()).Order());
        Assert.Equal(["failed", "failed", "passed", "passed", "passed"], seen.Select(s => s["result"]!.GetValue<string>())); // worst first
        Assert.Equal(5, seen[0]["phases"]!.AsArray().Count);

        var missing = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, LoadTestTools.Run.Method, "{}"));
        Assert.Equal("run_id", Assert.Single(missing["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_sees_only_the_runs_of_its_scope()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var a = rig.Camera("10.0.0.1");
        var b = rig.Camera("10.0.0.2");
        var keyA = rig.Ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = rig.Ctx.FakeScopes.Add("Building B", [b.Id]);
        var anna = FakeCallContext.WithGroup(rig.Ctx, [a.Id], "anna", keyA);
        var bert = FakeCallContext.WithGroup(rig.Ctx, [b.Id], "bert", keyB);

        var started = await rig.StartCheckAsync([a], call: anna);
        await rig.RunToEndAsync();

        Assert.Single(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, anna, LoadTestTools.Runs.Method, null))["runs"]!.AsArray());
        Assert.Empty(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, bert, LoadTestTools.Runs.Method, null))["runs"]!.AsArray());
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Ctx.InvokeAsync(rig.Plugin, bert, LoadTestTools.Run.Method, $$"""{"run_id":"{{started.RunId}}"}"""));
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var started = await rig.StartCheckAsync(rig.Cameras(2));
        await rig.RunToEndAsync();
        var before = Stored(rig.Ctx);
        var requests = rig.Ctx.Cameras.Values.Sum(c => c.Requests.Count);

        ICorePlugin core = rig.Plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, tool.Method, $$"""{"run_id":"{{started.RunId}}"}"""));
            Assert.NotNull(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, tool.Method, $$"""{"run_id":"{{started.RunId}}","limit":1}"""));
        }

        Assert.Equal(before, Stored(rig.Ctx));
        Assert.Equal(requests, rig.Ctx.Cameras.Values.Sum(c => c.Requests.Count)); // no camera was asked
    }

    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;
}
