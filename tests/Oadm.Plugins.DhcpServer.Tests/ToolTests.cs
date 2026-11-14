using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The tools of the DHCP server (docs/spec/mcp.md "Plugins"): status and leases, read only, with the page's device group rules.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_tools_read_like_the_page_and_the_leases_are_paged_by_mac_address()
    {
        var ctx = new Testing.FakeCoreContext();
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("dhcp", core.ToolArea);
        Assert.Equal(["status", "leases"], core.Tools.Select(t => t.Name));
        Assert.All(core.Tools, t =>
        {
            Assert.Equal(UserRole.Operator, core.RequiredRole(t.Method));
            Assert.False(core.IsAudited(t.Method));
            Assert.False(core.RequiresAllDevices(t.Method));
        });

        var admin = ctx.CallAsAllDevices();
        for (var i = 1; i <= 7; i++)
        {
            await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest($"AC:CC:8E:00:00:0{i}", $"10.0.0.{100 + i}", $"gate {i}")));
        }

        var status = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, Tools.DhcpTools.Status.Method, null))!)!;
        Assert.False(status["enabled"]!.GetValue<bool>());
        Assert.Equal(7, status["static_leases"]!.GetValue<int>());

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 5; page++)
        {
            var payload = new JsonObject { ["limit"] = 3 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var leases = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, Tools.DhcpTools.Leases.Method, payload.ToJsonString()))!)!;
            Assert.Equal(7, leases["total"]!.GetValue<int>());
            seen.AddRange(leases["leases"]!.AsArray().Select(l => l!["mac"]!.GetValue<string>()));
            after = leases["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(seen.Order(StringComparer.Ordinal), seen);

        var reserved = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, Tools.DhcpTools.Leases.Method, """{"state":"reserved","search":"gate 3"}"""))!)!;
        Assert.Equal("AC:CC:8E:00:00:03", Assert.Single(reserved["leases"]!.AsArray())!["mac"]!.GetValue<string>());
        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, Tools.DhcpTools.Leases.Method, """{"state":"asleep"}"""))!)!;
        Assert.Equal("state", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_gets_the_leases_of_its_own_devices_only()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" }); // serial B8A44F631339 = its MAC
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Lobby", [inside.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "guard", key);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("B8:A4:4F:63:13:39", "10.0.0.148", null)));
        await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("AC:CC:8E:00:00:02", "10.0.0.149", null)));

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, Tools.DhcpTools.Leases.Method, null))!)!;
        Assert.Equal(["B8:A4:4F:63:13:39"], mine["leases"]!.AsArray().Select(l => l!["mac"]!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, Tools.DhcpTools.Leases.Method, null))!)!["total"]!.GetValue<int>());
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new Testing.FakeCoreContext();
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest("AC:CC:8E:00:00:01", "10.0.0.101", "gate 1")));
        string Everything() => Stored(ctx) + "\n" + DhcpJson.Serialize(plugin.Service!.GetState(full: true)); // the leases live in memory and are stored later
        var before = Everything();
        Assert.Contains("gate 1", before, StringComparison.Ordinal); // there is something to compare

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, null));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"state":"reserved","limit":1}"""));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"state":"asleep"}"""));
        }

        Assert.Equal(before, Everything());
    }

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(Testing.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;
}
