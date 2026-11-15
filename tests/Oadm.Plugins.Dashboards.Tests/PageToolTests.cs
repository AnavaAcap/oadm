using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Plugins.Dashboards.Tools;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): read a dashboard and the events, save, delete
/// and press a button, with the rights of the page methods they wrap; a preview stores and sends nothing.
/// </summary>
public sealed class PageToolTests
{
    private static readonly Guid A = Guid.NewGuid();

    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    private static string Stored(PluginHost host) => string.Join('\n', host.Settings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));

    private static (string Field, string Error) ErrorOf(JsonNode answer) =>
        (answer["errors"]![0]!["field"]!.GetValue<string>(), answer["errors"]![0]!["error"]!.GetValue<string>());

    private const string ButtonWidget = """{"kind":"button","x":0,"y":0,"width":4,"height":2,"name":"Open gate"}""";

    [Fact]
    public void Each_tool_has_the_right_of_its_page_method_and_a_preview_is_never_audited()
    {
        ICorePlugin core = new DashboardsPlugin();
        Assert.Equal(["list", "values", "get", "events", "save", "delete", "press"], core.Tools.Select(t => t.Name));
        foreach (var tool in DashboardPageTools.Set.Tools)
        {
            var page = DashboardPageTools.Set.PageMethodOf(tool.Method)!;
            Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(tool.Method));
            Assert.Equal(core.RequiredRole(page), core.RequiredRole(tool.Method));
            Assert.Equal(core.IsAudited(page), core.IsAudited(tool.Method));
            Assert.False(core.IsAudited(tool.PreviewMethod));
        }

        Assert.Equal(DashboardsPluginInfo.ManageKey, core.RequiredPermission("tool.save.preview"));
        Assert.Equal(DashboardsPluginInfo.PressKey, core.RequiredPermission("tool.press"));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.get"));
        Assert.True(DashboardPageTools.Set.ToolOf("tool.delete")!.Destructive);
    }

    [Fact]
    public async Task Save_checks_like_the_page_the_preview_stores_nothing_and_get_answers_the_widgets()
    {
        using var host = new PluginHost();
        var before = Stored(host);
        var args = $$"""{"name":"Gate house","widgets":[{{ButtonWidget}}]}""";
        var plan = Answer(await host.Plugin.InvokeAsync("tool.save.preview", args, CancellationToken.None));
        Assert.Equal(("create", 1, 1), (plan["change"]!.GetValue<string>(), plan["widgets"]!.GetValue<int>(), plan["buttons"]!.GetValue<int>()));
        Assert.Equal((1, "dashboard"), (plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
        Assert.Equal(before, Stored(host));
        Assert.Empty((await host.Plugin.ListAsync(CancellationToken.None)).Dashboards);

        var saved = Answer(await host.Plugin.InvokeAsync("tool.save", args, CancellationToken.None));
        var id = saved["dashboard_id"]!.GetValue<string>();
        var dashboard = Assert.Single((await host.Plugin.ListAsync(CancellationToken.None)).Dashboards);
        Assert.Equal(("Gate house", WidgetKind.Button), (dashboard.Name, Assert.Single(dashboard.Widgets).Kind));

        // The page's own texts below the fields.
        Assert.Equal(("name", DashboardRules.NameTaken("Gate house")), ErrorOf(Answer(await host.Plugin.InvokeAsync("tool.save.preview", args, CancellationToken.None))));
        Assert.Equal(("name", DashboardRules.NameMissing), ErrorOf(Answer(await host.Plugin.InvokeAsync("tool.save.preview", """{"name":" "}""", CancellationToken.None))));
        var overlap = $$"""{"name":"Two","widgets":[{{ButtonWidget}},{"kind":"button","x":1,"y":0,"width":4,"height":2,"name":"Close"}]}""";
        Assert.Equal(("widgets", DashboardRules.Overlap), ErrorOf(Answer(await host.Plugin.InvokeAsync("tool.save.preview", overlap, CancellationToken.None))));

        // A rename keeps the widgets; get answers them with their settings.
        await host.Plugin.InvokeAsync("tool.save", $$"""{"dashboard_id":"{{id}}","name":"Gate"}""", CancellationToken.None);
        var got = Answer(await host.Plugin.InvokeAsync("tool.get", $$"""{"dashboard_id":"{{id}}"}""", CancellationToken.None));
        Assert.Equal("Gate", got["name"]!.GetValue<string>());
        Assert.Equal(("button", "Open gate"), (got["widgets"]![0]!["kind"]!.GetValue<string>(), got["widgets"]![0]!["name"]!.GetValue<string>()));
        Assert.Equal(dashboard.Widgets[0].Id, got["widgets"]![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Press_previews_without_sending_and_the_run_sends_the_buttons_event_once()
    {
        using var host = new PluginHost();
        var gate = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Gate house", 0,
            [new Widget { Id = "open", Kind = WidgetKind.Button, X = 0, Y = 0, Width = 4, Height = 2, Name = "Open gate" }]), CancellationToken.None);
        var args = $$"""{"dashboard_id":"{{gate.Id}}","widget_id":"open"}""";

        var plan = Answer(await host.Plugin.InvokeAsync("tool.press.preview", args, CancellationToken.None));
        Assert.Equal(("Gate house", "Open gate"), (plan["dashboard"]!.GetValue<string>(), plan["button"]!.GetValue<string>()));
        Assert.Equal(DashboardRules.NoRule, plan["warnings"]![0]!.GetValue<string>());
        Assert.Empty(host.Publisher.Fired);
        host.Publisher.Rules.Add(DashboardsPluginInfo.ButtonTypeId(gate.Id, "open"));
        Assert.Equal(DashboardPageTools.RulesMayAct, Answer(await host.Plugin.InvokeAsync("tool.press.preview", args, CancellationToken.None))["warnings"]![0]!.GetValue<string>());

        var pressed = Answer(await host.Plugin.InvokeAsync("tool.press", args, CancellationToken.None));
        Assert.True(pressed["used_by_rule"]!.GetValue<bool>());
        Assert.Single(host.Publisher.Fired);
        Assert.Equal("Pressed Open gate on Gate house", host.Plugin.AuditDetail("tool.press", args));

        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Plugin.InvokeAsync("tool.press.preview", $$"""{"dashboard_id":"{{gate.Id}}","widget_id":"none"}""", CancellationToken.None));
        Assert.Equal(DashboardRules.ButtonGone, gone.Message);
        Assert.Equal("widget_id", ErrorOf(Answer(await host.Plugin.InvokeAsync("tool.press.preview", $$"""{"dashboard_id":"{{gate.Id}}"}""", CancellationToken.None))).Field);
        Assert.Single(host.Publisher.Fired);
    }

    [Fact]
    public async Task Delete_previews_the_dashboard_and_a_second_run_finds_it_gone()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Hall", 0,
            [new Widget { Id = "open", Kind = WidgetKind.Button, X = 0, Y = 0, Width = 4, Height = 2, Name = "Open" }]), CancellationToken.None);
        var args = $$"""{"dashboard_id":"{{hall.Id}}"}""";
        var plan = Answer(await host.Plugin.InvokeAsync("tool.delete.preview", args, CancellationToken.None));
        Assert.Equal(("Hall", 1), (plan["name"]!.GetValue<string>(), plan["buttons"]!.GetValue<int>()));
        Assert.Single((await host.Plugin.ListAsync(CancellationToken.None)).Dashboards);

        await host.Plugin.InvokeAsync("tool.delete", args, CancellationToken.None);
        Assert.Empty((await host.Plugin.ListAsync(CancellationToken.None)).Dashboards);
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Plugin.InvokeAsync("tool.delete", args, CancellationToken.None));
        Assert.Equal(DashboardRules.Gone, twice.Message);
    }

    [Fact]
    public async Task A_group_changes_and_presses_only_its_own_dashboards()
    {
        var fake = new Testing.FakeCoreContext();
        var a = fake.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = fake.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = fake.FakeScopes.Add("Building A", [a.Id]);
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(new FakeEventSubscriptions());
        ctx.EventPublisher.Returns(new FakePublisher());
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        using var plugin = new DashboardsPlugin(new FakeTimeProvider());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        var admin = fake.CallAsAllDevices();
        var theirs = await plugin.SaveAsync(admin, new SaveDashboardRequest(null, "Everything", 0,
            [new Widget { Id = "open", Kind = WidgetKind.Button, X = 0, Y = 0, Width = 4, Height = 2, Name = "Open" }]), CancellationToken.None);

        foreach (var method in new[] { "tool.delete.preview", "tool.get" })
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.InvokeAsync(anna, method, $$"""{"dashboard_id":"{{theirs.Id}}"}""", CancellationToken.None));
            Assert.Equal(DashboardRules.Gone, refused.Message);
        }

        var press = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.InvokeAsync(anna, "tool.press", $$"""{"dashboard_id":"{{theirs.Id}}","widget_id":"open"}""", CancellationToken.None));
        Assert.Equal(DashboardRules.ButtonGone, press.Message);

        // A widget of a device outside the group is refused before anything is stored; the group's own is saved for the group.
        var video = $$"""{"kind":"video","x":0,"y":0,"device_id":"{{b.Id}}"}""";
        await Assert.ThrowsAnyAsync<Exception>(() => plugin.InvokeAsync(anna, "tool.save", $$"""{"name":"Mine","widgets":[{{video}}]}""", CancellationToken.None));
        var own = $$"""{"kind":"video","x":0,"y":0,"device_id":"{{a.Id}}"}""";
        await plugin.InvokeAsync(anna, "tool.save", $$"""{"name":"Mine","widgets":[{{own}}]}""", CancellationToken.None);
        Assert.Equal(["Mine"], (await plugin.ListAsync(anna, CancellationToken.None)).Dashboards.Select(d => d.Name));
        Assert.Equal(2, (await plugin.ListAsync(admin, CancellationToken.None)).Dashboards.Count);
    }
}
