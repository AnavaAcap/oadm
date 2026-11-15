using System.Text.Json.Nodes;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Save, Delete and Read now of a text reader, each
/// a preview that stores nothing and a run through the page's method, with the page's right, audit and device group rules.
/// </summary>
public sealed class PageToolTests
{
    private static async Task<JsonNode> CallAsync(OcrHarness ocr, string method, string payload, FakeCallContext? caller = null) =>
        JsonNode.Parse((await ocr.Context.InvokeAsync(ocr.Plugin, caller ?? ocr.Context.CallAsAllDevices(), method, payload))!)!;

    private static string NewReader(Guid device, string name = "Gate 3", string mode = "on_action") =>
        $$$"""{"name":"{{{name}}}","device_id":"{{{device}}}","mode":"{{{mode}}}","areas":[{"name":"Plate","box":{"x":0.1,"y":0.1,"width":0.5,"height":0.5},"format":"@@ ###"}]}""";

    [Fact]
    public void The_tools_have_the_rights_and_audit_of_the_page_methods_they_wrap()
    {
        ICorePlugin core = new OcrPlugin();
        foreach (var tool in OcrPageTools.Set.Tools)
        {
            Assert.True(tool.Changes, tool.Name);
            Assert.Equal(OcrInfo.ChangePermission, core.RequiredPermission(tool.Method));
            Assert.Equal(OcrInfo.ChangePermission, core.RequiredPermission(tool.PreviewMethod));
            Assert.True(core.IsAudited(tool.RunMethod), tool.Name);
            Assert.False(core.IsAudited(tool.PreviewMethod), tool.Name); // a preview never writes
            Assert.False(core.RequiresAllDevices(tool.Method));
        }

        Assert.NotNull(core.SystemAccessReason("tool.reader_save.preview")); // the page's count of watching readers per site
        Assert.True(OcrPageTools.Set.ToolOf("tool.reader_delete")!.Destructive);
    }

    [Fact]
    public async Task Save_previews_without_storing_and_the_run_creates_and_changes_a_reader()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var writes = ocr.Context.PluginRecords.WrittenRecords;

        var plan = await CallAsync(ocr, "tool.reader_save.preview", NewReader(ocr.Inside.Id));
        Assert.Equal("create", plan["change"]!.GetValue<string>());
        Assert.Equal(1, plan["will_change"]!.GetValue<int>());
        Assert.Equal("reader", plan["unit"]!.GetValue<string>());
        Assert.Equal(["Plate (@@ ###)"], plan["areas"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Empty(ocr.Plugin.AllReaders);
        Assert.Equal(writes, ocr.Context.PluginRecords.WrittenRecords);

        var saved = await CallAsync(ocr, "tool.reader_save", NewReader(ocr.Inside.Id));
        var id = saved["reader_id"]!.GetValue<string>();
        var reader = Assert.Single(ocr.Plugin.AllReaders);
        Assert.Equal(("Gate 3", ReaderMode.OnAction, "@@ ###"), (reader.Name, reader.Mode, reader.Areas[0].Expected.Format));

        // A change keeps what it leaves out.
        var changed = await CallAsync(ocr, "tool.reader_save", $$"""{"reader_id":"{{id}}","name":"Gate 4"}""");
        Assert.Equal("change", changed["change"]!.GetValue<string>());
        reader = Assert.Single(ocr.Plugin.AllReaders);
        Assert.Equal(("Gate 4", 1), (reader.Name, reader.Areas.Count));
    }

    [Fact]
    public async Task Invalid_input_answers_the_page_texts_on_the_tools_fields_and_stores_nothing()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var bad = await CallAsync(ocr, "tool.reader_save.preview",
            $$$"""{"name":" ","device_id":"{{{ocr.Inside.Id}}}","areas":[{"name":"A","box":{"x":0.9,"y":0.1,"width":0.5,"height":0.5}}]}""");
        var errors = bad["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
        Assert.Equal(ReaderRules.NameMissing, errors["name"]);
        Assert.Equal(ReaderRules.AreaOutside, errors["areas.0.box"]);

        var mode = await CallAsync(ocr, "tool.reader_save.preview", """{"name":"X","mode":"sometimes"}""");
        Assert.Equal("mode", mode["errors"]![0]!["field"]!.GetValue<string>());
        var camera = await CallAsync(ocr, "tool.reader_save.preview", """{"name":"X"}""");
        Assert.Contains(camera["errors"]!.AsArray(), e => e!["field"]!.GetValue<string>() == "device_id" && e["error"]!.GetValue<string>() == ReaderRules.CameraMissing);

        // The run checks again: an invalid run stores nothing either.
        var run = await CallAsync(ocr, "tool.reader_save", """{"name":"X"}""");
        Assert.NotNull(run["errors"]);
        Assert.Empty(ocr.Plugin.AllReaders);
    }

    [Fact]
    public async Task Delete_runs_once_and_a_second_run_is_refused_with_the_page_text()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Lobby"));
        var plan = await CallAsync(ocr, "tool.reader_delete.preview", $$"""{"reader_id":"{{reader.Id}}"}""");
        Assert.Equal("Lobby", plan["name"]!.GetValue<string>());
        Assert.Single(ocr.Plugin.AllReaders);

        await CallAsync(ocr, "tool.reader_delete", $$"""{"reader_id":"{{reader.Id}}"}""");
        Assert.Empty(ocr.Plugin.AllReaders);
        var again = await Assert.ThrowsAsync<ArgumentException>(() => ocr.Context.InvokeAsync(ocr.Plugin, ocr.Context.CallAsAllDevices(), "tool.reader_delete", $$"""{"reader_id":"{{reader.Id}}"}"""));
        Assert.Equal(ReaderRules.Gone, again.Message);
    }

    [Fact]
    public async Task Read_now_answers_the_text_and_only_an_on_action_reader_reads_now()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var gate = await ocr.SaveAsync(ocr.NewReader("Gate"));
        ocr.Frames.Single = "AB123";

        var plan = await CallAsync(ocr, "tool.reader_read_now.preview", $$"""{"reader_id":"{{gate.Id}}"}""");
        Assert.Equal(OcrPageTools.FiresEvents, plan["warnings"]![0]!.GetValue<string>());
        Assert.Equal(0, ocr.Frames.Singles); // the preview reads nothing

        var read = await CallAsync(ocr, "tool.reader_read_now", $$"""{"reader_id":"{{gate.Id}}"}""");
        Assert.Equal("AB123", read["areas"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("cutout", read.ToJsonString(), StringComparison.OrdinalIgnoreCase); // never a picture

        var watching = await ocr.SaveAsync(ocr.NewReader("Belt", ReaderMode.Watching));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.Context.InvokeAsync(ocr.Plugin, ocr.Context.CallAsAllDevices(), "tool.reader_read_now.preview", $$"""{"reader_id":"{{watching.Id}}"}"""));
        Assert.Equal(ReaderRules.ReadNowOnActionOnly, refused.Message);
    }

    [Fact]
    public async Task A_group_saves_reads_and_deletes_only_its_own_readers_and_cameras()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var lobby = await ocr.SaveAsync(ocr.NewReader("Lobby"));

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ocr.Context.InvokeAsync(ocr.Plugin, group, "tool.reader_save.preview", NewReader(ocr.Outside.Id)));
        foreach (var method in new[] { "tool.reader_delete.preview", "tool.reader_read_now.preview", "tool.reader_delete" })
        {
            var gone = await Assert.ThrowsAsync<ArgumentException>(() => ocr.Context.InvokeAsync(ocr.Plugin, group, method, $$"""{"reader_id":"{{lobby.Id}}"}"""));
            Assert.Equal(ReaderRules.Gone, gone.Message); // another scope's reader answers like an unknown one
        }

        await Assert.ThrowsAsync<ArgumentException>(() => ocr.Context.InvokeAsync(ocr.Plugin, group, "tool.reader_save", $$"""{"reader_id":"{{lobby.Id}}","name":"Mine"}"""));
        var mine = await CallAsync(ocr, "tool.reader_save", NewReader(ocr.Inside.Id, "Gate"), group);
        Assert.Equal(key, ocr.Plugin.FindReader(mine["reader_id"]!.GetValue<string>())!.OwnerScopeKey);
        Assert.Equal(2, ocr.Plugin.AllReaders.Count);
        Assert.Equal("Lobby", ocr.Plugin.FindReader(lobby.Id)!.Name);
    }
}
