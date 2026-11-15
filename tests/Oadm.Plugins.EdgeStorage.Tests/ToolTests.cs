using System.Text.Json.Nodes;

using Oadm.Plugins.EdgeStorage.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>The tool of Edge storage (docs/spec/mcp.md "Plugins"): the table the page read last, of the caller's cameras, paged; it reads no camera.</summary>
public sealed class ToolTests
{
    private static readonly IReadOnlyList<DeviceApi> Slot = [new("disk-management", "1.0"), new("disk-properties", "1.1")];

    private static DeviceSweepOptions Options() => new() { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) };

    [Fact]
    public async Task The_cards_are_the_last_read_table_paged_by_camera_and_read_no_camera()
    {
        var ctx = new Testing.FakeCoreContext();
        var cameras = Enumerable.Range(1, 5).Select(i => ctx.Add(new Testing.FakeDevice { Address = $"10.0.0.{i}", Apis = Slot })).ToList();
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("cards", Assert.Single(core.Tools).Name);
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(StorageTools.Cards.Method));
        Assert.False(core.IsAudited(StorageTools.Cards.Method));
        var all = ctx.CallAsAllDevices();

        // Before a read: an empty table, and the tool contacts nobody.
        var before = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, null))!)!;
        Assert.Equal(0, before["total"]!.GetValue<int>());
        Assert.Null(before["read_utc"]);
        Assert.Empty(ctx.ClientsCreated);

        await ctx.InvokeAsync(plugin, all, EdgeStorageMethods.Read, null);
        await plugin.Sweep!.Current;
        var created = ctx.ClientsCreated.Values.Sum();

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 6; page++)
        {
            var payload = new JsonObject { ["limit"] = 2 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, payload.ToJsonString()))!)!;
            Assert.Equal(5, answer["total"]!.GetValue<int>());
            Assert.NotNull(answer["read_utc"]);
            seen.AddRange(answer["cards"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(cameras.Select(c => c.Id.ToString()).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(created, ctx.ClientsCreated.Values.Sum()); // the tool read no camera

        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, """{"state":"melted"}"""))!)!;
        Assert.Equal("state", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
        var none = JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, """{"state":"ok"}"""))!)!;
        Assert.Equal(0, none["total"]!.GetValue<int>()); // the fakes answer no card list: every row is an error
    }

    [Fact]
    public async Task A_group_gets_the_cards_of_its_own_cameras_only()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var all = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, all, EdgeStorageMethods.Read, null);
        await plugin.Sweep!.Current;

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, StorageTools.Cards.Method, null))!)!;
        Assert.Equal(0, mine["total"]!.GetValue<int>()); // the group's own table was never read
        await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.Read, null);
        await plugin.Sweeps!.For(key)!.Current;
        mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, StorageTools.Cards.Method, null))!)!;
        Assert.Equal(inside.Id.ToString(), Assert.Single(mine["cards"]!.AsArray())!["device_id"]!.GetValue<string>());
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, all, StorageTools.Cards.Method, null))!)!["total"]!.GetValue<int>());
    }
}
