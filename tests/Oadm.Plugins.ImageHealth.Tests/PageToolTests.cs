using System.Net;
using System.Text.Json.Nodes;

using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Plugins.ImageHealth.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.ImageHealth.Tests;

/// <summary>
/// The page's Check now as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): the rights of the page method, the chosen
/// cameras of the caller's view only, field errors for no choice.
/// </summary>
public sealed class PageToolTests
{
    private const string Normal = """{"block":"normal","blur":"normal","redirect":"normal","under-exposure":"normal","unsuitability":"suitable"}""";

    private static string Ids(params Guid[] ids) => new JsonArray([.. ids.Select(i => (JsonNode)i.ToString())]).ToJsonString();

    [Fact]
    public void Check_has_the_rights_of_the_page_method()
    {
        ICorePlugin core = new ImageHealthPlugin();
        Assert.Equal(ImageHealthMethods.Check, ImageHealthPageTools.Set.PageMethodOf("tool.check"));
        Assert.Equal(core.RequiredRole(ImageHealthMethods.Check), core.RequiredRole("tool.check"));
        Assert.Equal(core.RequiredPermission(ImageHealthMethods.Check), core.RequiredPermission("tool.check"));
        Assert.Equal(core.RequiresAllDevices(ImageHealthMethods.Check), core.RequiresAllDevices("tool.check"));
        Assert.False(core.IsAudited("tool.check"));
        var tool = Assert.Single(ImageHealthPageTools.Set.Tools);
        Assert.True(tool.DeviceFilter);
        Assert.False(tool.Changes); // the page checks without asking
    }

    [Fact]
    public async Task Check_asks_the_chosen_cameras_and_the_results_show_them()
    {
        var ctx = new FakeCoreContext();
        var a = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        var b = ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera());
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var started = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.check", $$"""{"device_ids":{{Ids(a.Id)}},"devices_total":1}"""))!)!;
        Assert.NotNull(started["total"]);
        await plugin.Monitor!.CurrentCheck;
        Assert.True(ctx.ClientsCreated.ContainsKey(a.Id));
        Assert.False(ctx.ClientsCreated.ContainsKey(b.Id));
        var results = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, ImageHealthTools.Results.Method, null))!)!;
        Assert.Equal([a.Id.ToString()], results["results"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));

        await ctx.InvokeAsync(plugin, admin, "tool.check", """{"all_devices":true}""");
        await plugin.Monitor.CurrentCheck;
        Assert.True(ctx.ClientsCreated.ContainsKey(b.Id));
    }

    [Fact]
    public async Task No_choice_is_refused_with_a_field_error_and_asks_no_camera()
    {
        var ctx = new FakeCoreContext();
        ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var none = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.check", """{"device_ids":[],"devices_total":0}"""))!)!;
        Assert.Equal(ImageHealthPageTools.ChooseCameras, none["errors"]![0]!["error"]!.GetValue<string>());
        var many = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, "tool.check", $$"""{"device_ids":["{{Guid.NewGuid()}}"],"devices_total":600}"""))!)!;
        Assert.Equal("filter", many["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Empty(ctx.ClientsCreated);
    }

    [Fact]
    public async Task A_group_checks_only_its_own_cameras()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new FakeDevice { Address = "10.0.0.1" }, Camera());
        var outside = ctx.Add(new FakeDevice { Address = "10.0.0.2" }, Camera());
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new ImageHealthPlugin(Fast());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);

        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, group, "tool.check", $$"""{"device_ids":{{Ids(outside.Id)}},"devices_total":1}"""));
        await ctx.InvokeAsync(plugin, group, "tool.check", """{"all_devices":true}""");
        await plugin.Monitor!.CurrentCheckOf(key);
        Assert.True(ctx.ClientsCreated.ContainsKey(inside.Id));
        Assert.False(ctx.ClientsCreated.ContainsKey(outside.Id));
    }

    private static FakeCamera Camera() => new()
    {
        Respond = (request, _) => request.RequestUri!.OriginalString.Contains(ImageHealthPluginInfo.StatusPath, StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Normal) }
            : null,
    };

    private static ImageHealthOptions Fast() => new() { PublishInterval = TimeSpan.FromHours(1), RequestTimeout = TimeSpan.FromSeconds(30) };
}
