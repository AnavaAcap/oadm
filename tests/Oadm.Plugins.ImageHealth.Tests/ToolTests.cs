using System.Net;
using System.Text.Json.Nodes;

using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Plugins.ImageHealth.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.ImageHealth.Tests;

/// <summary>The tool of the Image Health Dashboard (docs/spec/mcp.md "Plugins"): the caller's last check, counts first, paged, never a camera read.</summary>
public sealed class ToolTests
{
    private const string Normal = """{"block":"normal","blur":"normal","redirect":"normal","under-exposure":"normal","unsuitability":"suitable"}""";
    private const string Blurred = """{"block":"normal","blur":"detected","redirect":"normal","under-exposure":"detected","unsuitability":"suitable"}""";

    [Fact]
    public async Task The_results_are_the_counts_and_one_row_per_camera_paged_and_filtered_by_detection()
    {
        var ctx = new FakeCoreContext();
        var cameras = Enumerable.Range(1, 4).Select(i => ctx.Add(new FakeDevice { Address = $"10.0.0.{i}" }, Camera(i == 2 ? Blurred : Normal))).ToList();
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("results", Assert.Single(core.Tools).Name);
        Assert.False(core.IsAudited(ImageHealthTools.Results.Method));
        var admin = ctx.CallAsAllDevices();

        var empty = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, null))!)!;
        Assert.Equal(ImageHealthTools.NothingChecked, empty["note"]!.GetValue<string>());
        Assert.Empty(ctx.ClientsCreated);

        await ctx.InvokeAsync(plugin, admin, ImageHealthMethods.Check, null);
        await plugin.Monitor!.CurrentCheck;
        var asked = ctx.ClientsCreated.Values.Sum();

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 5; page++)
        {
            var payload = new JsonObject { ["limit"] = 3 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, payload.ToJsonString()))!)!;
            Assert.Equal(4, answer["total"]!.GetValue<int>());
            Assert.Equal(4, answer["counts"]!["running"]!.GetValue<int>());
            Assert.Equal(1, answer["counts"]!["blur"]!.GetValue<int>());
            seen.AddRange(answer["results"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(cameras.Select(d => d.Id.ToString()).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(asked, ctx.ClientsCreated.Values.Sum()); // the tool asked no camera

        var blurred = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, """{"detected":"blur"}"""))!)!;
        var row = Assert.Single(blurred["results"]!.AsArray())!;
        Assert.Equal(cameras[1].Id.ToString(), row["device_id"]!.GetValue<string>());
        Assert.Equal("running", row["app"]!.GetValue<string>());
        Assert.Equal("detected", row["blur"]!.GetValue<string>());
        Assert.Equal("ok", row["block"]!.GetValue<string>());
        Assert.Equal(1, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, """{"detected":"any"}"""))!)!["total"]!.GetValue<int>());
        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, """{"detected":"fog"}"""))!)!;
        Assert.Equal("detected", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_gets_the_results_of_its_own_cameras_only()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera(Normal));
        ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera(Normal));
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, group, ImageHealthMethods.Check, null);
        await plugin.Monitor!.CurrentCheckOf(key);
        await ctx.InvokeAsync(plugin, admin, ImageHealthMethods.Check, null);
        await plugin.Monitor.CurrentCheck;

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, ImageHealthTools.Results.Method, null))!)!;
        Assert.Equal([inside.Id.ToString()], mine["results"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, null))!)!["total"]!.GetValue<int>());
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new FakeCoreContext();
        ctx.Add(new FakeDevice { Address = "10.0.0.7" }, Camera(Normal));
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, ImageHealthMethods.Check, null);
        await plugin.Monitor!.CurrentCheck;
        var before = Stored(ctx);

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, null));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"detected":"blur","limit":1}"""));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"detected":"fog"}""")); // an invalid call too
        }

        Assert.Equal(before, Stored(ctx));
    }

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;

    private static FakeCamera Camera(string status) => new()
    {
        Respond = (request, _) => request.RequestUri!.OriginalString.Contains(ImageHealthPluginInfo.StatusPath, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(status) }
            : null,
    };

    private static ImageHealthOptions Fast() => new() { PublishInterval = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(30) };
}
