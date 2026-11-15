using System.Text.Json.Nodes;

using Oadm.Plugins.LoadTest.Checking;
using Oadm.Plugins.LoadTest.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Plan, Start (preview and run), Cancel and
/// Delete check (preview and run), with the rights of their page methods, the page's texts and every safety of the check.
/// </summary>
public sealed class PageToolTests
{
    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    private static string Ids(IEnumerable<Oadm.Sdk.Devices.IDeviceInfo> devices)
    {
        var list = devices.ToList();
        return $$"""{{new JsonArray([.. list.Select(d => (JsonNode)d.Id.ToString())]).ToJsonString()}}""";
    }

    private static string Chosen(IEnumerable<Oadm.Sdk.Devices.IDeviceInfo> devices, string extra = "")
    {
        var list = devices.ToList();
        return $$"""{"device_ids":{{Ids(list)}},"devices_total":{{list.Count}}{{extra}}}""";
    }

    [Fact]
    public void The_tools_have_the_rights_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new LoadTestPlugin();
        Assert.Equal(["checks", "check", "plan", "start", "cancel", "check_delete"], core.Tools.Select(t => t.Name));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.plan"));
        foreach (var method in new[] { "tool.start", "tool.start.preview", "tool.cancel", "tool.check_delete", "tool.check_delete.preview" })
        {
            Assert.Equal(LoadTestInfo.RunPermission, core.RequiredPermission(method));
            Assert.Equal(UserRole.Operator, core.RequiredRole(method));
            Assert.False(core.IsAudited(method));
        }

        // The exposure goes back with full access in the run only, like the page's Start; the preview only reads.
        Assert.NotNull(core.SystemAccessReason("tool.start"));
        Assert.Null(core.SystemAccessReason("tool.start.preview"));
        Assert.True(LoadTestPageTools.Set.ToolOf("tool.start")!.Changes);
        Assert.True(LoadTestPageTools.Set.ToolOf("tool.check_delete")!.Destructive);
        Assert.True(LoadTestPageTools.Set.ToolOf("tool.plan")!.DeviceFilter);
        Assert.All(core.Tools, t => Assert.False(t.Name.EndsWith("_preview", StringComparison.Ordinal) || t.Name.EndsWith("_run", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Plan_and_the_start_preview_change_nothing_and_the_run_starts_one_check()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var cameras = rig.Cameras(3);

        var plan = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.plan", Chosen(cameras, ""","batch_size":2""")));
        Assert.Equal(3, plan["streams"]!.GetValue<int>());
        Assert.Equal(2, plan["batches"]!.GetValue<int>());
        Assert.Null(plan["refusal"]);

        var preview = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start.preview", Chosen(cameras, ""","phase_minutes":1""")));
        Assert.Equal(3, preview["will_change"]!.GetValue<int>());
        Assert.Equal("camera stream", preview["unit"]!.GetValue<string>());
        Assert.Contains("exposure", preview["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(rig.Service.Runner); // nothing started
        Assert.Empty(rig.Service.ListRuns(rig.Admin).Runs);
        Assert.All(cameras, c => Assert.Equal(Rig.Exposure, rig.Ctx.Cameras[c.Id].Parameters[ExposureKeeper.ParameterOf(1)]));

        var started = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start", Chosen(cameras, ""","phase_minutes":1""")));
        Assert.Equal("running", started["state"]!.GetValue<string>());
        Assert.Equal(3, started["streams"]!.GetValue<int>());

        // One check at a time: the preview and the run say so.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start.preview", Chosen(cameras, ""","phase_minutes":1""")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start", Chosen(cameras, ""","phase_minutes":1""")));

        // Deleting a running check: the page's text.
        var running = started["run_id"]!.GetValue<string>();
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.check_delete.preview", $$"""{"run_id":"{{running}}"}"""));
        Assert.Equal("Cancel the check first.", first.Message);

        var cancelled = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.cancel", null));
        Assert.True(cancelled["cancelled"]!.GetValue<bool>());
        Assert.Equal("cancelled", cancelled["check"]!["state"]!.GetValue<string>());
        Assert.All(cameras, c => Assert.Equal(Rig.Exposure, rig.Ctx.Cameras[c.Id].Parameters[ExposureKeeper.ParameterOf(1)])); // always back
        Assert.False(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.cancel", null))["cancelled"]!.GetValue<bool>());

        var delete = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.check_delete.preview", $$"""{"run_id":"{{running}}"}"""));
        Assert.Equal("check", delete["unit"]!.GetValue<string>());
        Assert.Single(rig.Service.ListRuns(rig.Admin).Runs); // the preview kept it
        await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.check_delete", $$"""{"run_id":"{{running}}"}""");
        Assert.Empty(rig.Service.ListRuns(rig.Admin).Runs);
        var gone = await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.check_delete", $$"""{"run_id":"{{running}}"}"""));
        Assert.Equal("The check was deleted.", gone.Message);
    }

    [Fact]
    public async Task Invalid_input_gets_the_page_texts_and_the_stream_limit_holds()
    {
        await using var rig = await new Rig().StartAsync();
        var cameras = rig.Cameras(2);

        var bad = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start.preview", Chosen(cameras, ""","batch_size":201,"phase_minutes":3""")));
        var errors = bad["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
        Assert.Equal("Choose 1 to 200 cameras at once.", errors["batch_size"]);
        Assert.Equal("Choose 1, 2 or 5 minutes per step.", errors["phase_minutes"]);
        var none = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.plan", """{"device_ids":[],"devices_total":0}"""));
        Assert.Equal(LoadTestPageTools.ChooseDevices, none["errors"]![0]!["error"]!.GetValue<string>());
        Assert.Equal("run_id", Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.check_delete.preview", "{}"))["errors"]![0]!["field"]!.GetValue<string>());

        // More than 1,000 camera streams: the plan says why, the start is refused with the page's text.
        rig.Cameras(LoadTestInfo.MaxStreams - 1);
        var plan = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.plan", """{"all_devices":true}"""));
        Assert.Equal(LoadTestInfo.TooManyStreams, plan["refusal"]!.GetValue<string>());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, rig.Admin, "tool.start", """{"all_devices":true}"""));
        Assert.Equal(LoadTestInfo.TooManyStreams, refused.Message);
        Assert.Null(rig.Service.Runner);
    }

    [Fact]
    public async Task A_group_plans_starts_and_deletes_only_with_its_own_devices()
    {
        await using var rig = await new Rig(phaseSeconds: 1).StartAsync();
        var a = rig.Camera("10.0.0.1");
        var b = rig.Camera("10.0.0.2");
        var keyA = rig.Ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = rig.Ctx.FakeScopes.Add("Building B", [b.Id]);
        var anna = FakeCallContext.WithGroup(rig.Ctx, [a.Id], "anna", keyA);
        var bert = FakeCallContext.WithGroup(rig.Ctx, [b.Id], "bert", keyB);

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Ctx.InvokeAsync(rig.Plugin, anna, "tool.plan", Chosen([b])));
        Assert.Equal(1, Answer(await rig.Ctx.InvokeAsync(rig.Plugin, anna, "tool.plan", """{"all_devices":true}"""))["streams"]!.GetValue<int>());

        var started = Answer(await rig.Ctx.InvokeAsync(rig.Plugin, anna, "tool.start", """{"all_devices":true,"phase_minutes":1}"""));
        Assert.Equal(1, started["streams"]!.GetValue<int>());
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Ctx.InvokeAsync(rig.Plugin, bert, "tool.start.preview", """{"all_devices":true}"""));
        Assert.Equal(CheckService.CheckRunning, other.Message); // no user, no progress of another group
        Assert.False(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, bert, "tool.cancel", null))["cancelled"]!.GetValue<bool>());

        Assert.True(Answer(await rig.Ctx.InvokeAsync(rig.Plugin, anna, "tool.cancel", null))["cancelled"]!.GetValue<bool>());
        var id = started["run_id"]!.GetValue<string>();
        var hidden = await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Ctx.InvokeAsync(rig.Plugin, bert, "tool.check_delete.preview", $$"""{"run_id":"{{id}}"}"""));
        Assert.Equal("The check was deleted.", hidden.Message); // another group's check answers like a deleted one
        Assert.Single(rig.Service.ListRuns(anna).Runs);
    }
}
