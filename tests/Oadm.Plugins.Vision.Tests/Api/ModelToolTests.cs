using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Tests.Models;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Api;

/// <summary>
/// The model tools and Use GPU (decision 2026-11-15): a model upload from the caller's uploaded file through the page's steps and
/// its model check, change and delete of a model, the graphics card setting; the rights of the page methods ("models" or
/// "change", users with all devices); a preview stores nothing; never a model file in an answer.
/// </summary>
public sealed class ModelToolTests
{
    private static byte[] Yolo(int classes = 3) => OnnxModel.Picture().Output("output0", [1, 4 + classes, 50], new float[(4 + classes) * 50]).Build();

    /// <summary>A model with a node of another operator domain: the page's check refuses it.</summary>
    private static byte[] CustomOperators()
    {
        var graph = new Proto().Message(1, new Proto().String(2, "x").String(4, "Custom").String(7, "com.example.custom")).ToArray();
        return [.. Yolo(), (7 << 3) | 2, (byte)graph.Length, .. graph];
    }

    private sealed class Real : IAsyncDisposable
    {
        private readonly TempFolder _data = new();

        public Real()
        {
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
            Context = new FakeCoreContext { DataDirectory = _data.Path };
            Plugin = new VisionPlugin(new VisionParts
            {
                Models = ctx => new ModelLibrary(ctx.Records!, _data.Path, time),
                Inference = _ => new FakeInference(),
                Frames = _ => new FakeFrames(),
                Time = time,
            });
            Caller = Context.CallAsAllDevices();
        }

        public FakeCoreContext Context { get; }

        public VisionPlugin Plugin { get; }

        public FakeCallContext Caller { get; }

        public FakeUploadedFiles Files => (FakeUploadedFiles)Caller.Uploads;

        public async Task<JsonNode> CallAsync(string method, JsonObject payload) =>
            JsonNode.Parse((await Context.InvokeAsync(Plugin, Caller, method, payload.ToJsonString()))!)!;

        public async ValueTask DisposeAsync()
        {
            await Plugin.StopAsync(CancellationToken.None);
            _data.Dispose();
        }
    }

    [Fact]
    public void The_model_tools_have_the_rights_and_audit_of_the_page_methods()
    {
        ICorePlugin core = new VisionPlugin();
        foreach (var (tool, page) in new[] { ("model_upload", VisionMethods.FinishUpload), ("model_update", VisionMethods.UpdateModel), ("model_delete", VisionMethods.DeleteModel) })
        {
            Assert.Equal(page, VisionPageTools.Set.PageMethodOf("tool." + tool));
            Assert.Equal(VisionInfo.ModelsPermission, core.RequiredPermission("tool." + tool));
            Assert.Equal(VisionInfo.ModelsPermission, core.RequiredPermission("tool." + tool + ".preview"));
            Assert.True(core.RequiresAllDevices("tool." + tool + ".preview"));
            Assert.True(core.IsAudited("tool." + tool));
            Assert.False(core.IsAudited("tool." + tool + ".preview"));
        }

        Assert.Equal(VisionInfo.ChangePermission, core.RequiredPermission("tool.use_gpu"));
        Assert.True(core.RequiresAllDevices("tool.use_gpu"));
        Assert.True(core.IsAudited("tool.use_gpu"));
        Assert.True(VisionPageTools.Set.ToolOf("tool.model_delete")!.Destructive);
        Assert.False(VisionPageTools.Set.ToolOf("tool.model_upload")!.Destructive);
    }

    [Fact]
    public async Task An_upload_previews_without_storing_and_the_run_checks_and_stores_the_model_once_audited_by_name()
    {
        await using var vision = new Real();
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);
        var file = vision.Files.Add("gate-people.onnx", Yolo());
        var args = new JsonObject { ["file_id"] = file.Id, ["class_names"] = new JsonArray("person", "bicycle", "car") };

        var plan = await vision.CallAsync("tool.model_upload.preview", args);
        Assert.Equal(("gate-people", "model", 3), (plan["name"]!.GetValue<string>(), plan["unit"]!.GetValue<string>(), plan["class_names"]!.GetValue<int>()));
        Assert.Equal([VisionPageTools.CheckedInRun], plan["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Empty(vision.Plugin.Running.Models.All);

        var done = await vision.CallAsync("tool.model_upload", args);
        Assert.Equal(("gate-people", "detection", 3), (done["name"]!.GetValue<string>(), done["kind"]!.GetValue<string>(), done["class_count"]!.GetValue<int>()));
        var model = Assert.Single(vision.Plugin.Running.Models.All);
        Assert.Equal(["person", "bicycle", "car"], model.ClassNames);
        Assert.Equal("Uploaded vision model gate-people", ((ICorePlugin)vision.Plugin).AuditDetail("tool.model_upload", args.ToJsonString()));
        Assert.DoesNotContain("AAAA", done.ToJsonString(), StringComparison.Ordinal); // never the file
    }

    [Fact]
    public async Task A_model_with_other_operators_is_refused_by_the_run_and_a_wrong_file_by_the_preview()
    {
        await using var vision = new Real();
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);
        var custom = vision.Files.Add("custom.onnx", CustomOperators());

        var plan = await vision.CallAsync("tool.model_upload.preview", new JsonObject { ["file_id"] = custom.Id });
        Assert.Equal(1, plan["will_change"]!.GetValue<int>());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => vision.Context.InvokeAsync(vision.Plugin, vision.Caller, "tool.model_upload", new JsonObject { ["file_id"] = custom.Id }.ToJsonString()));
        Assert.Equal(ModelTexts.CannotRun(ModelTexts.OtherOperators("com.example.custom")), refused.Message);
        Assert.Empty(vision.Plugin.Running.Models.All);
        Assert.Equal("Refused vision model custom", ((ICorePlugin)vision.Plugin).AuditDetail("tool.model_upload", new JsonObject { ["file_id"] = custom.Id }.ToJsonString()));

        var text = vision.Files.Add("notes.txt", [1, 2, 3]);
        var wrong = await vision.CallAsync("tool.model_upload.preview", new JsonObject { ["file_id"] = text.Id });
        Assert.Equal(("file_id", ModelTexts.NotOnnxFile), (wrong["errors"]![0]!["field"]!.GetValue<string>(), wrong["errors"]![0]!["error"]!.GetValue<string>()));
        var missing = await vision.CallAsync("tool.model_upload.preview", new JsonObject { ["file_id"] = Guid.NewGuid().ToString("N") });
        Assert.Equal(VisionPageTools.NoSuchFile, missing["errors"]![0]!["error"]!.GetValue<string>());

        // Another session's upload answers like an unknown id.
        var other = vision.Context.CallAsAllDevices("someone");
        var theirs = JsonNode.Parse((await vision.Context.InvokeAsync(vision.Plugin, other, "tool.model_upload.preview", new JsonObject { ["file_id"] = custom.Id }.ToJsonString()))!)!;
        Assert.Equal(VisionPageTools.NoSuchFile, theirs["errors"]![0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_change_and_a_delete_preview_first_and_run_once()
    {
        await using var vision = new Real();
        await vision.Plugin.StartAsync(vision.Context, CancellationToken.None);
        var file = vision.Files.Add("gate.onnx", Yolo(2));
        var id = (await vision.CallAsync("tool.model_upload", new JsonObject { ["file_id"] = file.Id }))["model_id"]!.GetValue<string>();

        var plan = await vision.CallAsync("tool.model_update.preview", new JsonObject { ["model_id"] = id, ["name"] = "Gate cars", ["class_names"] = new JsonArray("car", "truck") });
        Assert.Equal(("gate", "Gate cars"), (plan["name"]!.GetValue<string>(), plan["new_name"]!.GetValue<string>()));
        Assert.Equal("gate", vision.Plugin.Running.Models.Find(id)!.Name);
        var tooMany = await vision.CallAsync("tool.model_update.preview", new JsonObject { ["model_id"] = id, ["class_list_id"] = "nope" });
        Assert.Equal(("class_list_id", ModelTexts.ClassListGone), (tooMany["errors"]![0]!["field"]!.GetValue<string>(), tooMany["errors"]![0]!["error"]!.GetValue<string>()));

        await vision.CallAsync("tool.model_update", new JsonObject { ["model_id"] = id, ["name"] = "Gate cars", ["class_names"] = new JsonArray("car", "truck") });
        Assert.Equal(("Gate cars", "truck"), (vision.Plugin.Running.Models.Find(id)!.Name, vision.Plugin.Running.Models.Find(id)!.ClassNames[1]));
        Assert.Equal("Changed vision model Gate cars", ((ICorePlugin)vision.Plugin).AuditDetail("tool.model_update", new JsonObject { ["model_id"] = id }.ToJsonString()));

        var delete = await vision.CallAsync("tool.model_delete.preview", new JsonObject { ["model_id"] = id });
        Assert.Equal([VisionPageTools.FileDeleted], delete["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.NotNull(vision.Plugin.Running.Models.Find(id));
        await vision.CallAsync("tool.model_delete", new JsonObject { ["model_id"] = id });
        Assert.Null(vision.Plugin.Running.Models.Find(id));
        var gone = await vision.CallAsync("tool.model_delete", new JsonObject { ["model_id"] = id });
        Assert.Equal(ModelTexts.Gone, gone["errors"]![0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_model_a_pipeline_uses_is_not_deleted_and_the_upload_goes_in_the_page_chunks()
    {
        await using var vision = await VisionHarness.StartAsync();
        var pipeline = new JsonObject
        {
            ["name"] = "Gate 5",
            ["device_id"] = vision.Inside.Id.ToString(),
            ["mode"] = "on_action",
            ["nodes"] = new JsonArray(
                new JsonObject { ["id"] = "cam", ["kind"] = NodeKinds.Camera, ["name"] = "Camera" },
                new JsonObject { ["id"] = "det", ["kind"] = NodeKinds.Detect, ["name"] = "Detect people", ["settings"] = """{"model":"m1"}""" },
                new JsonObject { ["id"] = "ev", ["kind"] = NodeKinds.Event, ["name"] = "People" }),
            ["links"] = new JsonArray(new JsonObject { ["from"] = "cam", ["to"] = "det" }, new JsonObject { ["from"] = "det", ["to"] = "ev" }),
        };
        var admin = vision.Context.CallAsAllDevices();
        await vision.Context.InvokeAsync(vision.Plugin, admin, "tool.pipeline_save", pipeline.ToJsonString());

        var used = await Assert.ThrowsAsync<InvalidOperationException>(() => vision.Context.InvokeAsync(vision.Plugin, admin, "tool.model_delete.preview", """{"model_id":"m1"}"""));
        Assert.Equal(ModelTexts.UsedBy(["Gate 5"]), used.Message);
        Assert.Empty(vision.Models.Deleted);

        var big = new byte[(2 * VisionLimits.UploadChunkBytes) + 10];
        var file = ((FakeUploadedFiles)admin.Uploads).Add("cars.onnx", big);
        await vision.Context.InvokeAsync(vision.Plugin, admin, "tool.model_upload", new JsonObject { ["file_id"] = file.Id }.ToJsonString());
        Assert.Equal(("cars.onnx", big.LongLength, (string?)null), Assert.Single(vision.Models.Begun));
        Assert.Equal([0L, VisionLimits.UploadChunkBytes, 2L * VisionLimits.UploadChunkBytes], vision.Models.Chunks.Select(c => c.Offset));

        var group = vision.Context.CallAsGroup([vision.Inside.Id]);
        await Assert.ThrowsAnyAsync<Exception>(() => vision.Context.InvokeAsync(vision.Plugin, group, "tool.model_upload.preview", new JsonObject { ["file_id"] = file.Id }.ToJsonString()));
    }

    [Fact]
    public async Task Use_gpu_previews_the_setting_and_the_run_sets_it()
    {
        await using var vision = await VisionHarness.StartAsync();
        var admin = vision.Context.CallAsAllDevices();
        var plan = JsonNode.Parse((await vision.Context.InvokeAsync(vision.Plugin, admin, "tool.use_gpu.preview", """{"use_gpu":false}"""))!)!;
        Assert.Equal((true, false), (plan["use_gpu"]!.GetValue<bool>(), plan["new_use_gpu"]!.GetValue<bool>()));
        Assert.Equal([VisionPageTools.ToProcessor], plan["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.True(VisionJson.Deserialize<EngineInfo>(await vision.Context.InvokeAsync(vision.Plugin, admin, VisionMethods.GetEngine, null)).UseGpu);

        var done = JsonNode.Parse((await vision.Context.InvokeAsync(vision.Plugin, admin, "tool.use_gpu", """{"use_gpu":false}"""))!)!;
        Assert.False(done["use_gpu"]!.GetValue<bool>());
        Assert.False(VisionJson.Deserialize<EngineInfo>(await vision.Context.InvokeAsync(vision.Plugin, admin, VisionMethods.GetEngine, null)).UseGpu);
        Assert.Equal("Turned Use GPU off for Vision Center", ((ICorePlugin)vision.Plugin).AuditDetail("tool.use_gpu", """{"use_gpu":false}"""));
    }
}
