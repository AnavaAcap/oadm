using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.LiveView.Tests;

/// <summary>
/// The tools of Live View (docs/spec/mcp.md "Plugins", decision 2026-11-15): the saved views and the page's Save and Delete as a
/// preview and a run, with the page's checks, rights and device groups. No tool opens video.
/// </summary>
public sealed class ToolTests
{
    private static async Task<JsonObject> CallAsync(Testing.FakeCoreContext ctx, LiveViewPlugin plugin, ICallContext call, string method, JsonObject? payload = null) =>
        JsonNode.Parse((await ctx.InvokeAsync(plugin, call, method, payload?.ToJsonString()))!)!.AsObject();

    private static JsonObject Save(string name, params Guid[] cameras) => new()
    {
        ["name"] = name,
        ["tiles"] = new JsonArray([.. cameras.Select(c => (JsonNode)new JsonObject { ["device_id"] = c.ToString() })]),
    };

    private static string Stored(Testing.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    [Fact]
    public void The_tools_have_the_rights_of_the_page_methods_and_no_tool_streams()
    {
        ICorePlugin core = new LiveViewPlugin();
        Assert.Equal(["views", "view_save", "view_delete"], core.Tools.Select(t => t.Name));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.views"));
        foreach (var method in new[] { "tool.view_save", "tool.view_save.preview", "tool.view_delete", "tool.view_delete.preview" })
        {
            Assert.Equal(LiveViewPluginInfo.ManageKey, core.RequiredPermission(method));
        }

        Assert.True(core.IsAudited("tool.view_save"));
        Assert.False(core.IsAudited("tool.view_save.preview"));
        Assert.False(core.IsAudited("tool.views"));
        Assert.True(core.Tools.Single(t => t.Name == "view_delete").Destructive);
    }

    [Fact]
    public async Task Save_previews_with_the_pages_checks_writes_once_and_Delete_removes_it()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        var before = Stored(ctx);

        var plan = await CallAsync(ctx, plugin, admin, "tool.view_save.preview", Save("Hall", a.Id, b.Id));
        Assert.Equal("create", plan["change"]!.GetValue<string>());
        Assert.Equal(2, plan["cameras"]!.GetValue<int>());
        Assert.Equal("view", plan["unit"]!.GetValue<string>());
        Assert.Equal(before, Stored(ctx));
        Assert.Empty((await plugin.ListAsync(CancellationToken.None)).Views);

        var saved = await CallAsync(ctx, plugin, admin, "tool.view_save", Save("Hall", a.Id, b.Id));
        var view = Assert.Single((await plugin.ListAsync(CancellationToken.None)).Views);
        Assert.Equal(saved["id"]!.GetValue<string>(), view.Id);
        Assert.Equal(2, view.Tiles.Count);
        Assert.Null(ViewRules.TilesProblem(view.Tiles)); // the equal squares fit and never overlap

        var listed = await CallAsync(ctx, plugin, admin, "tool.views");
        Assert.Equal(1, listed["total"]!.GetValue<int>());
        Assert.Equal(2, listed["views"]![0]!["cameras"]!.AsArray().Count);

        var change = Save("Hall 2", a.Id);
        change["id"] = view.Id;
        Assert.Equal("change", (await CallAsync(ctx, plugin, admin, "tool.view_save.preview", change))["change"]!.GetValue<string>());

        var delete = await CallAsync(ctx, plugin, admin, "tool.view_delete.preview", new JsonObject { ["id"] = view.Id });
        Assert.Equal("Hall", delete["name"]!.GetValue<string>());
        Assert.Single((await plugin.ListAsync(CancellationToken.None)).Views);
        await CallAsync(ctx, plugin, admin, "tool.view_delete", new JsonObject { ["id"] = view.Id });
        Assert.Empty((await plugin.ListAsync(CancellationToken.None)).Views);
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, admin, "tool.view_delete", new JsonObject { ["id"] = view.Id }.ToJsonString()));
        Assert.Equal(ViewRules.Gone, again.Message);
    }

    [Fact]
    public async Task Invalid_views_are_refused_with_the_pages_texts_and_nothing_is_written()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await CallAsync(ctx, plugin, admin, "tool.view_save", Save("Hall", a.Id));
        var before = Stored(ctx);

        var noName = await CallAsync(ctx, plugin, admin, "tool.view_save.preview", Save(" ", a.Id));
        Assert.Equal(ViewRules.NameMissing, noName["errors"]![0]!["error"]!.GetValue<string>());
        var overlap = new JsonObject
        {
            ["name"] = "Overlap",
            ["tiles"] = new JsonArray(
                new JsonObject { ["device_id"] = a.Id.ToString(), ["x"] = 0, ["y"] = 0, ["width"] = 4, ["height"] = 4 },
                new JsonObject { ["device_id"] = a.Id.ToString(), ["x"] = 2, ["y"] = 2, ["width"] = 4, ["height"] = 4 }),
        };
        Assert.Equal(ViewRules.Overlap, (await CallAsync(ctx, plugin, admin, "tool.view_save.preview", overlap))["errors"]![0]!["error"]!.GetValue<string>());
        var half = new JsonObject
        {
            ["name"] = "Half",
            ["tiles"] = new JsonArray(new JsonObject { ["device_id"] = a.Id.ToString(), ["x"] = 0, ["y"] = 0, ["width"] = 4, ["height"] = 4 }, new JsonObject { ["device_id"] = a.Id.ToString() }),
        };
        Assert.Equal(LiveViewTools.AllOrNoPlaces, (await CallAsync(ctx, plugin, admin, "tool.view_save.preview", half))["errors"]![0]!["error"]!.GetValue<string>());
        var taken = await Assert.ThrowsAsync<ArgumentException>(() => ctx.InvokeAsync(plugin, admin, "tool.view_save.preview", Save("hall", a.Id).ToJsonString()));
        Assert.Equal(ViewRules.NameTaken("hall"), taken.Message);
        Assert.Equal(before, Stored(ctx));
    }

    [Fact]
    public async Task A_group_sees_and_saves_only_its_views_with_its_cameras()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id]);
        using var plugin = new LiveViewPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        var admin = ctx.CallAsAllDevices();
        var adminView = (await CallAsync(ctx, plugin, admin, "tool.view_save", Save("Everything", a.Id, b.Id)))["id"]!.GetValue<string>();

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ctx.InvokeAsync(plugin, anna, "tool.view_save.preview", Save("Mine", a.Id, b.Id).ToJsonString()));
        var mine = await CallAsync(ctx, plugin, anna, "tool.view_save", Save("Mine", a.Id));
        Assert.Equal(keyA, (await plugin.ListAsync(CancellationToken.None)).Views.Single(v => v.Id == mine["id"]!.GetValue<string>()).Owner);

        var seen = await CallAsync(ctx, plugin, anna, "tool.views");
        Assert.Equal(1, seen["total"]!.GetValue<int>());
        Assert.DoesNotContain(b.Id.ToString(), seen.ToJsonString(), StringComparison.Ordinal);
        var other = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, anna, "tool.view_delete.preview", new JsonObject { ["id"] = adminView }.ToJsonString()));
        Assert.Equal(ViewRules.Gone, other.Message);
    }
}
