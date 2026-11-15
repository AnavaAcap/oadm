using System.Text.Json.Nodes;

using Oadm.Plugins.EdgeStorage.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>The tool of Edge storage (docs/spec/mcp.md "Plugins"): the caller's table, counts first, paged, never a camera read.</summary>
public sealed class ToolTests
{
    private static readonly IReadOnlyList<DeviceApi> Slot = [new("disk-management", "1.0"), new("disk-properties", "1.1")];

    [Fact]
    public async Task The_cards_are_the_counts_and_the_rows_of_the_last_read_paged_by_device()
    {
        var ctx = new Testing.FakeCoreContext();
        var cameras = Enumerable.Range(1, 5).Select(i => ctx.Add(new Testing.FakeDevice { Address = $"10.0.0.{i}", Apis = Slot })).ToList();
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("cards", Assert.Single(core.Tools).Name);
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(EdgeStorageTools.Cards.Method));
        Assert.False(core.IsAudited(EdgeStorageTools.Cards.Method));
        var admin = ctx.CallAsAllDevices();

        // Before a read: nothing, and the tool reads no camera.
        var empty = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, null))!)!;
        Assert.Equal(0, empty["total"]!.GetValue<int>());
        Assert.Equal(EdgeStorageTools.NothingRead, empty["note"]!.GetValue<string>());
        Assert.Empty(ctx.ClientsCreated);

        await ctx.InvokeAsync(plugin, admin, EdgeStorageMethods.Read, null);
        await plugin.Sweeps!.For(admin)!.Current;
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

            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, payload.ToJsonString()))!)!;
            Assert.Equal(5, answer["total"]!.GetValue<int>());
            Assert.Equal(5, answer["counts"]!["error"]!.GetValue<int>()); // the fake cameras answer nothing an SD card list reads
            Assert.Null(answer["note"]);
            seen.AddRange(answer["cards"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(cameras.Select(d => d.Id.ToString()).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(created, ctx.ClientsCreated.Values.Sum()); // the tool asked no camera

        var row = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, """{"state":"error","limit":1}"""))!)!["cards"]![0]!;
        Assert.Equal("error", row["state"]!.GetValue<string>());
        Assert.StartsWith("10.0.0.", row["address"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, """{"state":"ok"}"""))!)!["total"]!.GetValue<int>());
        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, """{"state":"full"}"""))!)!;
        Assert.Equal("state", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public void A_card_row_has_its_size_use_wear_and_retention()
    {
        var row = new StorageRow(Guid.NewGuid(), "10.0.0.9", "AXIS Q1615", CardStates.Warning, "Worn", TotalKb: 64L * 1024 * 1024, FreeKb: 16L * 1024 * 1024, WearPercent: 92, WearAlertPercent: 90, Encrypted: true, RetentionDays: 30, Mounted: true, ReadUtc: DateTimeOffset.UnixEpoch);
        var state = new StorageState(false, 1, 1, 0, 1, 0, 0, 0, DateTimeOffset.UnixEpoch, [row]);
        var card = JsonNode.Parse(EdgeStorageTools.Answer(state, null))!["cards"]![0]!;
        Assert.Equal("warning", card["state"]!.GetValue<string>());
        Assert.Equal(64.0, card["size_gb"]!.GetValue<double>());
        Assert.Equal(75, card["used_percent"]!.GetValue<int>());
        Assert.Equal(92, card["wear_percent"]!.GetValue<int>());
        Assert.True(card["encrypted"]!.GetValue<bool>());
        Assert.Equal(30, card["retention_days"]!.GetValue<int>());
        Assert.Equal("1970-01-01T00:00:00Z", card["read_utc"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_gets_the_cards_of_its_own_table_only()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1", Apis = Slot });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2", Apis = Slot });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, EdgeStorageMethods.Read, null);
        await plugin.Sweeps!.For(admin)!.Current;
        await ctx.InvokeAsync(plugin, group, EdgeStorageMethods.Read, null);
        await plugin.Sweeps!.For(key)!.Current;

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, EdgeStorageTools.Cards.Method, null))!)!;
        Assert.Equal([inside.Id.ToString()], mine["cards"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, EdgeStorageTools.Cards.Method, null))!)!["total"]!.GetValue<int>());
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new Testing.FakeCoreContext();
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.7", Apis = Slot });
        await using var plugin = new EdgeStoragePlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, EdgeStorageMethods.Read, null);
        await plugin.Sweeps!.For(admin)!.Current;
        var before = Stored(ctx);

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, null));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"state":"ok","limit":1}"""));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"state":"full"}""")); // an invalid call too
        }

        Assert.Equal(before, Stored(ctx));
        Assert.Empty(ctx.TaskRuns);
    }

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(Testing.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;

    private static DeviceSweepOptions Options() => new() { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) };
}
