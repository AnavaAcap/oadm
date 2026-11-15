using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.Pipelines;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Vision.Tests.Api;

/// <summary>The tool of Vision Center (docs/spec/mcp.md "Plugins"): the page's pipeline list and the load, of the caller's scope only.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_pipelines_follow_the_page_and_a_group_sees_only_its_own()
    {
        await using var vision = await VisionHarness.StartAsync();
        ICorePlugin core = vision.Plugin;
        Assert.Equal("pipelines", Assert.Single(core.Tools).Name);
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(VisionTools.Pipelines.Method));
        Assert.False(core.IsAudited(VisionTools.Pipelines.Method));
        var key = vision.Context.FakeScopes.Add("Building B", [vision.Inside.Id]);
        var group = FakeCallContext.WithGroup(vision.Context, [vision.Inside.Id], "operator", key);
        var lobby = await vision.SaveAsync(vision.NewPipeline("Lobby", PipelineMode.OnAction));
        await vision.SaveAsync(vision.NewPipeline("Gate", PipelineMode.OnAction), group);

        async Task<JsonNode> ToolAsync(string? payload, FakeCallContext? caller = null) =>
            JsonNode.Parse((await vision.Context.InvokeAsync(vision.Plugin, caller ?? vision.Context.CallAsAllDevices(), VisionTools.Pipelines.Method, payload))!)!;

        var all = await ToolAsync(null);
        Assert.Equal(2, all["total"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(all["load"]!["state"]!.GetValue<string>()));
        var mine = await ToolAsync(null, group);
        var row = Assert.Single(mine["pipelines"]!.AsArray())!;
        Assert.Equal("Gate", row["name"]!.GetValue<string>());
        Assert.Equal("on_action", row["mode"]!.GetValue<string>());
        Assert.DoesNotContain(lobby.Id, mine.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, (await ToolAsync("""{"search":"lob"}"""))["total"]!.GetValue<int>());

        // Paging by id: two pages of one, each once.
        var first = await ToolAsync("""{"limit":1}""");
        var second = await ToolAsync(new JsonObject { ["limit"] = 1, ["after"] = first["next"]!.GetValue<string>() }.ToJsonString());
        Assert.NotEqual(first["pipelines"]![0]!["id"]!.GetValue<string>(), second["pipelines"]![0]!["id"]!.GetValue<string>());
        Assert.Null(second["next"]);
    }
}
