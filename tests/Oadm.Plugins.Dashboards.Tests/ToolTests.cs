using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using NSubstitute;

using Oadm.Plugins.Dashboards.Tools;
using Oadm.Sdk.Events;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Dashboards.Tests;

/// <summary>The tools of Dashboards (docs/spec/mcp.md "Plugins"): the caller's dashboards and the values their widgets show now, read only.</summary>
public sealed class ToolTests
{
    private static readonly Guid A = Guid.NewGuid();

    private const string Door = "oadm.device-triggers.door";

    private static Widget Number(string id, int x, Guid device, string typeId = Types.Temperature) => new()
    {
        Id = id, Kind = WidgetKind.Number, X = x, Y = 0, TypeId = typeId, Field = "temperature", DeviceId = device, Name = "Temperature", Unit = "C",
    };

    private static JsonNode Answer(string? json) => JsonNode.Parse(json!)!;

    [Fact]
    public async Task The_list_counts_the_widgets_and_the_values_are_what_the_widgets_show_now()
    {
        using var host = new PluginHost();
        ICorePlugin core = host.Plugin;
        Assert.Equal(["list", "values"], core.Tools.Select(t => t.Name));
        Assert.All(core.Tools, t => Assert.Equal(PermissionKeys.Show, core.RequiredPermission(t.Method)));
        Assert.All(core.Tools, t => Assert.False(core.IsAudited(t.Method)));
        var hall = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Hall", 0,
            [Number("t", 0, A), new Widget { Id = "v", Kind = WidgetKind.Video, X = 4, Y = 0, DeviceId = A }]), CancellationToken.None);
        await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Entrance", 0, []), CancellationToken.None);
        host.Feed.Publish(Types.Temperature, A, PublishedEventKind.Fired, ("temperature", "21.5"));

        var list = Answer(await host.Plugin.InvokeAsync(DashboardTools.List.Method, null, CancellationToken.None));
        Assert.Equal(2, list["total"]!.GetValue<int>());
        Assert.Equal(["Entrance", "Hall"], list["dashboards"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        var row = list["dashboards"]![1]!;
        Assert.Equal((2, 1, 1), (row["widgets"]!.GetValue<int>(), row["number"]!.GetValue<int>(), row["video"]!.GetValue<int>()));
        Assert.Equal(1, Answer(await host.Plugin.InvokeAsync(DashboardTools.List.Method, """{"search":"ent"}""", CancellationToken.None))["total"]!.GetValue<int>());
        var first = Answer(await host.Plugin.InvokeAsync(DashboardTools.List.Method, """{"limit":1}""", CancellationToken.None));
        var second = Answer(await host.Plugin.InvokeAsync(DashboardTools.List.Method, new JsonObject { ["limit"] = 1, ["after"] = first["next"]!.GetValue<string>() }.ToJsonString(), CancellationToken.None));
        Assert.Equal("Hall", second["dashboards"]![0]!["name"]!.GetValue<string>());
        Assert.Null(second["next"]);

        var values = Answer(await host.Plugin.InvokeAsync(DashboardTools.Values.Method, $$"""{"dashboard_id":"{{hall.Id}}"}""", CancellationToken.None));
        Assert.Equal(("Hall", 2), (values["name"]!.GetValue<string>(), values["total"]!.GetValue<int>()));
        var number = values["widgets"]![0]!;
        Assert.Equal(("number", "21.5", "C"), (number["kind"]!.GetValue<string>(), number["value"]!.GetValue<string>(), number["unit"]!.GetValue<string>()));
        Assert.Equal(A.ToString(), number["device_id"]!.GetValue<string>());
        Assert.NotNull(number["time"]);
        var video = values["widgets"]![1]!;
        Assert.Equal("video", video["kind"]!.GetValue<string>());
        Assert.Null(video["value"]);

        Assert.Equal("dashboard_id", Answer(await host.Plugin.InvokeAsync(DashboardTools.Values.Method, "{}", CancellationToken.None))["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(DashboardTools.NoDashboard, Answer(await host.Plugin.InvokeAsync(DashboardTools.Values.Method, """{"dashboard_id":"none"}""", CancellationToken.None))["errors"]![0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_sees_only_its_dashboards_and_the_values_of_its_devices()
    {
        var fake = new Testing.FakeCoreContext();
        var a = fake.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = fake.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = fake.FakeScopes.Add("Building A", [a.Id]);
        var feed = new FakeEventSubscriptions();
        feed.TypeList.Add(new(new EventTypeInfo(Door, "Door", "Device triggers", EventKind.Stateless, EventScope.Device, [new EventField("temperature", "Temperature")]) { DeviceIds = [a.Id, b.Id] },
            DashboardsPluginInfo.DeviceTriggersId, "Device triggers", true));
        var ctx = Substitute.For<ICorePluginContext>();
        ctx.Settings.Returns(fake.PluginSettings);
        ctx.Events.Returns(fake);
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.EventSubscriptions.Returns(feed);
        ctx.DeviceScopes.Returns(fake.FakeScopes);
        ctx.Jobs.Returns(fake.FakeJobs);
        using var plugin = new DashboardsPlugin(new FakeTimeProvider());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(fake, [a.Id], "anna", keyA);
        var admin = fake.CallAsAllDevices();

        var mine = await plugin.SaveAsync(anna, new SaveDashboardRequest(null, "Gate A", 0, [Number("t", 0, a.Id, Door)]), CancellationToken.None);
        var theirs = await plugin.SaveAsync(admin, new SaveDashboardRequest(null, "Everything", 0, [Number("t", 0, a.Id, Door), Number("u", 4, b.Id, Door)]), CancellationToken.None);
        feed.Publish(Door, a.Id, PublishedEventKind.Fired, ("temperature", "19"));
        feed.Publish(Door, b.Id, PublishedEventKind.Fired, ("temperature", "30"));

        var list = Answer(await plugin.InvokeAsync(anna, DashboardTools.List.Method, null, CancellationToken.None));
        Assert.Equal(["Gate A"], list["dashboards"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()));
        Assert.Equal(2, Answer(await plugin.InvokeAsync(admin, DashboardTools.List.Method, null, CancellationToken.None))["total"]!.GetValue<int>());
        Assert.Equal("19", Answer(await plugin.InvokeAsync(anna, DashboardTools.Values.Method, $$"""{"dashboard_id":"{{mine.Id}}"}""", CancellationToken.None))["widgets"]![0]!["value"]!.GetValue<string>());
        var refused = Answer(await plugin.InvokeAsync(anna, DashboardTools.Values.Method, $$"""{"dashboard_id":"{{theirs.Id}}"}""", CancellationToken.None));
        Assert.Equal(DashboardTools.NoDashboard, refused["errors"]![0]!["error"]!.GetValue<string>());
        var everything = Answer(await plugin.InvokeAsync(admin, DashboardTools.Values.Method, $$"""{"dashboard_id":"{{theirs.Id}}"}""", CancellationToken.None));
        Assert.Equal(["19", "30"], everything["widgets"]!.AsArray().Select(w => w!["value"]!.GetValue<string>())); // the group never got the second
        Assert.Empty(fake.RefusedAccesses);
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        using var host = new PluginHost();
        var hall = await host.Plugin.SaveAsync(new SaveDashboardRequest(null, "Hall", 0, [Number("t", 0, A)]), CancellationToken.None);
        var before = string.Join('\n', host.Settings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
        var published = host.Events.Of(DashboardsMethods.DashboardsTopic).Count;

        ICorePlugin core = host.Plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await host.Plugin.InvokeAsync(tool.Method, null, CancellationToken.None));
            Assert.NotNull(await host.Plugin.InvokeAsync(tool.Method, $$"""{"dashboard_id":"{{hall.Id}}","limit":1}""", CancellationToken.None));
        }

        Assert.Equal(before, string.Join('\n', host.Settings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)));
        Assert.Equal(published, host.Events.Of(DashboardsMethods.DashboardsTopic).Count);
        Assert.Empty(host.Publisher.Fired);
    }
}
