using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Plugins.Vision.Tests.Models;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Api;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): one pipeline, Save, Copy and Delete of a
/// pipeline (the graph check's one error per node), the models, the class lists and Save of a class list, with the rights of
/// the page methods; a preview stores nothing, a run goes through the page's method.
/// </summary>
public sealed class PageToolTests
{
    private static async Task<JsonNode> CallAsync(VisionHarness vision, string method, JsonObject payload, FakeCallContext? caller = null) =>
        JsonNode.Parse((await vision.Context.InvokeAsync(vision.Plugin, caller ?? vision.Context.CallAsAllDevices(), method, payload.ToJsonString()))!)!;

    private static JsonObject Node(string id, string kind, string name, string? settings = null) =>
        new() { ["id"] = id, ["kind"] = kind, ["name"] = name, ["settings"] = settings };

    private static JsonObject NewPipeline(Guid device, string name = "Gate 5", string model = "m1") => new()
    {
        ["name"] = name,
        ["device_id"] = device.ToString(),
        ["mode"] = "on_action",
        ["nodes"] = new JsonArray(
            Node("cam", NodeKinds.Camera, "Camera"),
            Node("det", NodeKinds.Detect, "Detect people", $$"""{"model":"{{model}}"}"""),
            Node("ev", NodeKinds.Event, "People")),
        ["links"] = new JsonArray(new JsonObject { ["from"] = "cam", ["to"] = "det" }, new JsonObject { ["from"] = "det", ["to"] = "ev" }),
    };

    [Fact]
    public void The_tools_have_the_rights_and_audit_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new VisionPlugin();
        foreach (var name in new[] { "pipeline_save", "pipeline_copy", "pipeline_delete" })
        {
            var tool = VisionPageTools.Set.Tools.Single(t => t.Name == name);
            Assert.True(tool.Changes);
            Assert.Equal(VisionInfo.ChangePermission, core.RequiredPermission(tool.PreviewMethod));
            Assert.Equal(VisionInfo.ChangePermission, core.RequiredPermission(tool.RunMethod));
            Assert.True(core.IsAudited(tool.RunMethod));
            Assert.False(core.IsAudited(tool.PreviewMethod));
            Assert.False(core.RequiresAllDevices(tool.RunMethod));
        }

        // Class lists are server-wide like the models: the right "models" and users with all devices, as on the page.
        Assert.Equal(VisionInfo.ModelsPermission, core.RequiredPermission("tool.class_list_save.preview"));
        Assert.True(core.RequiresAllDevices("tool.class_list_save"));
        Assert.True(core.RequiresAllDevices("tool.class_list_save.preview"));
        foreach (var read in new[] { "tool.pipeline", "tool.models", "tool.class_lists" })
        {
            Assert.Equal(PermissionKeys.Show, core.RequiredPermission(read));
            Assert.False(core.IsAudited(read));
            Assert.False(core.RequiresAllDevices(read));
        }

        Assert.NotNull(core.SystemAccessReason("tool.pipeline_save.preview"));
        Assert.True(VisionPageTools.Set.ToolOf("tool.pipeline_delete")!.Destructive);
        Assert.True(VisionPageTools.Set.ToolOf("tool.pipeline_copy")!.DeviceFilter);
    }

    [Fact]
    public async Task Save_previews_without_storing_and_the_run_creates_a_pipeline_the_read_answers()
    {
        await using var vision = await VisionHarness.StartAsync();
        var writes = vision.Context.PluginRecords.WrittenRecords;
        var plan = await CallAsync(vision, "tool.pipeline_save.preview", NewPipeline(vision.Inside.Id));
        Assert.Equal(("create", 1, "pipeline"), (plan["change"]!.GetValue<string>(), plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
        Assert.Equal(3, plan["nodes"]!.AsArray().Count);
        Assert.Empty((await vision.ListAsync()).Pipelines);
        Assert.Equal(writes, vision.Context.PluginRecords.WrittenRecords);

        var saved = await CallAsync(vision, "tool.pipeline_save", NewPipeline(vision.Inside.Id));
        var id = saved["pipeline_id"]!.GetValue<string>();
        var read = await CallAsync(vision, "tool.pipeline", new JsonObject { ["pipeline_id"] = id });
        Assert.Equal("Gate 5", read["name"]!.GetValue<string>());
        Assert.Equal("on_action", read["mode"]!.GetValue<string>());
        Assert.Equal("m1", JsonNode.Parse(read["nodes"]![1]!["settings"]!.GetValue<string>())!["model"]!.GetValue<string>());
        Assert.Equal(2, read["links"]!.AsArray().Count);

        // A change keeps what it leaves out.
        await CallAsync(vision, "tool.pipeline_save", new JsonObject { ["pipeline_id"] = id, ["name"] = "Gate 6" });
        var row = Assert.Single((await vision.ListAsync()).Pipelines);
        Assert.Equal("Gate 6", row.Name);
        Assert.Equal(3, vision.Plugin.Running.Store.Find(id)!.Nodes.Count);
    }

    [Fact]
    public async Task An_invalid_graph_names_the_node_and_nothing_is_stored()
    {
        await using var vision = await VisionHarness.StartAsync();
        var bad = await CallAsync(vision, "tool.pipeline_save.preview", NewPipeline(vision.Inside.Id, model: "nope"));
        var error = Assert.Single(bad["errors"]!.AsArray())!;
        Assert.Equal("nodes.1", error["field"]!.GetValue<string>());
        Assert.Equal("Detect people: Choose a model.", error["error"]!.GetValue<string>()); // the graph check's text

        var unreadable = NewPipeline(vision.Inside.Id);
        unreadable["nodes"]![1]!["settings"] = "{model";
        Assert.Equal("nodes.1.settings", (await CallAsync(vision, "tool.pipeline_save.preview", unreadable))["errors"]![0]!["field"]!.GetValue<string>());
        var unnamed = NewPipeline(vision.Inside.Id, name: " ");
        Assert.Equal("name", (await CallAsync(vision, "tool.pipeline_save", unnamed))["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Empty((await vision.ListAsync()).Pipelines);
    }

    [Fact]
    public async Task Copy_skips_the_own_camera_and_delete_runs_once()
    {
        await using var vision = await VisionHarness.StartAsync();
        var original = await vision.SaveAsync(vision.NewPipeline("Lobby", PipelineMode.OnAction));
        var copy = new JsonObject
        {
            ["pipeline_id"] = original.Id,
            ["device_ids"] = new JsonArray(vision.Inside.Id.ToString(), vision.Outside.Id.ToString()),
            ["devices_total"] = 2,
        };

        var plan = await CallAsync(vision, "tool.pipeline_copy.preview", copy);
        Assert.Equal(1, plan["will_change"]!.GetValue<int>());
        Assert.Equal(PipelineRules.OwnCamera, plan["skipped"]![0]!["reason"]!.GetValue<string>());
        Assert.Null(plan["device_ids"]); // kept for the run, never shown
        Assert.Single((await vision.ListAsync()).Pipelines);

        var copied = await CallAsync(vision, "tool.pipeline_copy", copy);
        Assert.Equal(1, copied["created_count"]!.GetValue<int>());
        Assert.Equal(2, (await vision.ListAsync()).Pipelines.Count);

        var tooMany = (JsonObject)copy.DeepClone();
        tooMany["devices_total"] = PipelineRules.MaxCopyCameras + 1;
        Assert.Equal(PipelineRules.TooManyCopyCameras, (await CallAsync(vision, "tool.pipeline_copy.preview", tooMany))["errors"]![0]!["error"]!.GetValue<string>());

        var delete = new JsonObject { ["pipeline_id"] = original.Id };
        Assert.Equal("Lobby", (await CallAsync(vision, "tool.pipeline_delete.preview", delete))["name"]!.GetValue<string>());
        await CallAsync(vision, "tool.pipeline_delete", delete);
        Assert.Single((await vision.ListAsync()).Pipelines);
        var again = await Assert.ThrowsAsync<ArgumentException>(() => vision.Context.InvokeAsync(vision.Plugin, vision.Context.CallAsAllDevices(), "tool.pipeline_delete", delete.ToJsonString()));
        Assert.Equal(PipelineRules.Gone, again.Message);
    }

    [Fact]
    public async Task A_group_reads_saves_copies_and_deletes_only_its_own_pipelines_and_cameras()
    {
        await using var vision = await VisionHarness.StartAsync();
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        var lobby = await vision.SaveAsync(vision.NewPipeline("Lobby", PipelineMode.OnAction));

        foreach (var method in new[] { "tool.pipeline", "tool.pipeline_delete.preview", "tool.pipeline_delete" })
        {
            var gone = await Assert.ThrowsAsync<ArgumentException>(() => vision.Context.InvokeAsync(vision.Plugin, group, method, new JsonObject { ["pipeline_id"] = lobby.Id }.ToJsonString()));
            Assert.Equal(PipelineRules.Gone, gone.Message); // another scope's pipeline answers like an unknown one
        }

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => vision.Context.InvokeAsync(vision.Plugin, group, "tool.pipeline_save.preview", NewPipeline(vision.Outside.Id).ToJsonString()));
        var mine = await CallAsync(vision, "tool.pipeline_save", NewPipeline(vision.Inside.Id), group);
        Assert.Equal(key, vision.Plugin.Running.Store.Find(mine["pipeline_id"]!.GetValue<string>())!.OwnerScopeKey);

        // The models: only the names of the caller's own pipelines.
        var models = await CallAsync(vision, "tool.models", [], group);
        Assert.Equal(["Gate 5"], models["models"]![0]!["used_by"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Lobby", vision.Plugin.Running.Store.Find(lobby.Id)!.Name);
    }

    [Fact]
    public async Task A_class_list_save_previews_with_the_page_checks_and_the_run_stores_it_once()
    {
        using var data = new TempFolder();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var context = new FakeCoreContext { DataDirectory = data.Path };
        var plugin = new VisionPlugin(new VisionParts
        {
            Models = ctx => new ModelLibrary(ctx.Records!, data.Path, time),
            Inference = _ => new FakeInference(),
            Frames = _ => new FakeFrames(),
            Time = time,
        });
        await plugin.StartAsync(context, CancellationToken.None);
        try
        {
            async Task<JsonNode> Call(string method, JsonObject payload) =>
                JsonNode.Parse((await context.InvokeAsync(plugin, context.CallAsAllDevices(), method, payload.ToJsonString()))!)!;

            var list = new JsonObject { ["name"] = "Gate", ["names"] = new JsonArray("person", "car") };
            var plan = await Call("tool.class_list_save.preview", list);
            Assert.Equal(("create", 2, "class list"), (plan["change"]!.GetValue<string>(), plan["count"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
            Assert.Equal(2, (await Call("tool.class_lists", [])) ["lists"]!.AsArray().Count); // the built-in ones only

            var saved = await Call("tool.class_list_save", list);
            Assert.Equal("create", saved["change"]!.GetValue<string>());
            Assert.Equal(3, (await Call("tool.class_lists", []))["lists"]!.AsArray().Count);

            var unnamed = await Call("tool.class_list_save.preview", new JsonObject { ["name"] = " ", ["names"] = new JsonArray("a") });
            Assert.Equal(("name", "Enter a name."), (unnamed["errors"]![0]!["field"]!.GetValue<string>(), unnamed["errors"]![0]!["error"]!.GetValue<string>()));
            var taken = await Call("tool.class_list_save.preview", list);
            Assert.Equal("name", taken["errors"]![0]!["field"]!.GetValue<string>());
            var builtIn = await Assert.ThrowsAsync<ArgumentException>(() => context.InvokeAsync(plugin, context.CallAsAllDevices(), "tool.class_list_save.preview",
                new JsonObject { ["class_list_id"] = ClassLists.Coco80Id, ["name"] = "Mine", ["names"] = new JsonArray("a") }.ToJsonString()));
            Assert.False(string.IsNullOrEmpty(builtIn.Message));
            Assert.Equal(3, (await Call("tool.class_lists", []))["lists"]!.AsArray().Count);

            var group = context.CallAsGroup([], "operator");
            await Assert.ThrowsAnyAsync<Exception>(() => context.InvokeAsync(plugin, group, "tool.class_list_save.preview", list.ToJsonString()));
        }
        finally
        {
            await plugin.StopAsync(CancellationToken.None);
        }
    }
}
