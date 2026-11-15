using System.Text.Json.Nodes;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>The tools of Text recognition (docs/spec/mcp.md "Plugins"): the readers and the recent texts of one, of the caller's scope only, never a picture.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_readers_and_their_texts_follow_the_page_and_a_group_sees_only_its_own()
    {
        await using var ocr = await OcrHarness.StartAsync();
        ICorePlugin core = ocr.Plugin;
        Assert.Equal(["readers", "reads", "reader_save", "reader_delete", "reader_read_now", "use_gpu"], core.Tools.Select(t => t.Name));
        Assert.All(OcrTools.All, t => Assert.Equal(PermissionKeys.Show, core.RequiredPermission(t.Method)));
        Assert.All(OcrTools.All, t => Assert.False(core.IsAudited(t.Method)));
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var lobby = await ocr.SaveAsync(ocr.NewReader("Lobby"));
        var gate = await ocr.SaveAsync(ocr.NewReader("Gate"), group);
        ocr.Frames.Single = "AB123";
        await ocr.ReadOnceAsync(gate.Id);

        async Task<JsonNode> ToolAsync(PluginTool tool, string? payload, FakeCallContext? caller = null) =>
            JsonNode.Parse((await ocr.Context.InvokeAsync(ocr.Plugin, caller ?? ocr.Context.CallAsAllDevices(), tool.Method, payload))!)!;

        var all = await ToolAsync(OcrTools.Readers, null);
        Assert.Equal(2, all["total"]!.GetValue<int>());
        var mine = await ToolAsync(OcrTools.Readers, null, group);
        var row = Assert.Single(mine["readers"]!.AsArray())!;
        Assert.Equal("Gate", row["name"]!.GetValue<string>());
        Assert.Equal("on_action", row["mode"]!.GetValue<string>());
        Assert.Equal("AB123", row["last_text"]!.GetValue<string>());
        Assert.DoesNotContain(lobby.Id, mine.ToJsonString(), StringComparison.Ordinal);

        var reads = await ToolAsync(OcrTools.Reads, $$"""{"reader_id":"{{gate.Id}}"}""", group);
        Assert.NotNull(reads["last_reading"]);
        Assert.DoesNotContain("cutout", reads.ToJsonString(), StringComparison.OrdinalIgnoreCase); // never a picture

        // Another scope's reader answers like an unknown one.
        await Assert.ThrowsAsync<ArgumentException>(() => ocr.Context.InvokeAsync(ocr.Plugin, group, OcrTools.Reads.Method, $$"""{"reader_id":"{{lobby.Id}}"}"""));
        var bad = await ToolAsync(OcrTools.Reads, "{}");
        Assert.Equal("reader_id", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }
}
