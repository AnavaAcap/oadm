using System.Text.Json.Nodes;

using Oadm.Plugins.EdgeStorage.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// The page's Read as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): the rights of the page method, the chosen cameras
/// of the caller's view only, a field error for no choice; the storage task stays a task.
/// </summary>
public sealed class PageToolTests
{
    private static readonly IReadOnlyList<DeviceApi> Slot = [new("disk-management", "1.0"), new("disk-properties", "1.1")];

    private static DeviceSweepOptions Options() => new() { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) };

    private static string Ids(params Guid[] ids) => new JsonArray([.. ids.Select(i => (JsonNode)i.ToString())]).ToJsonString();

    [Fact]
    public void Read_has_the_rights_of_the_page_method_and_is_no_change()
    {
        ICorePlugin core = new EdgeStoragePlugin();
        Assert.Equal(EdgeStorageMethods.Read, StoragePageTools.Set.PageMethodOf("tool.read"));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.read"));
        Assert.Equal(core.RequiredRole(EdgeStorageMethods.Read), core.RequiredRole("tool.read"));
        Assert.False(core.IsAudited("tool.read"));
        var tool = Assert.Single(StoragePageTools.Set.Tools);
        Assert.True(tool.DeviceFilter);
        Assert.False(tool.Changes);
        Assert.DoesNotContain(core.Tools, t => t.Name is "change" or "format" or "check"); // the storage task does those
    }

    [Fact]
    public async Task Read_reads_the_chosen_cameras_into_the_table_of_the_cards_tool()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var all = ctx.CallAsAllDevices();

        var started = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, "tool.read", $$"""{"device_ids":{{Ids(a.Id)}},"devices_total":1}"""))!)!;
        Assert.NotNull(started["total"]);
        await plugin.Sweep!.Current;
        Assert.True(ctx.ClientsCreated.ContainsKey(a.Id));
        Assert.False(ctx.ClientsCreated.ContainsKey(b.Id));
        var cards = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, null))!)!;
        Assert.Equal([a.Id.ToString()], cards["cards"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));

        await ctx.InvokeAsync(plugin, all, "tool.read", """{"all_devices":true}""");
        await plugin.Sweep.Current;
        Assert.True(ctx.ClientsCreated.ContainsKey(b.Id));
    }

    [Fact]
    public async Task No_choice_is_refused_with_a_field_error_and_reads_nothing()
    {
        var ctx = new Testing.FakeCoreContext();
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var all = ctx.CallAsAllDevices();

        var none = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, "tool.read", """{"device_ids":[],"devices_total":0}"""))!)!;
        Assert.Equal(StoragePageTools.ChooseCameras, none["errors"]![0]!["error"]!.GetValue<string>());
        var many = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, "tool.read", $$"""{"device_ids":["{{Guid.NewGuid()}}"],"devices_total":900}"""))!)!;
        Assert.Equal("filter", many["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Empty(ctx.ClientsCreated);
    }

    [Fact]
    public async Task A_group_reads_only_its_own_cameras()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);

        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, group, "tool.read", $$"""{"device_ids":{{Ids(outside.Id)}},"devices_total":1}"""));
        await ctx.InvokeAsync(plugin, group, "tool.read", """{"all_devices":true}""");
        await plugin.Sweeps!.For(key)!.Current;
        Assert.True(ctx.ClientsCreated.ContainsKey(inside.Id));
        Assert.Equal(0, ctx.ClientsCreated.GetValueOrDefault(outside.Id));
    }
}
