using System.Net;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.NtpServer.Serving;
using Oadm.Plugins.NtpServer.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>The tool of the NTP server (docs/spec/mcp.md "Plugins"): the status, read only, with the page's device group rules.</summary>
public sealed class ToolTests
{
    [Fact]
    public async Task The_status_reads_like_the_page_and_a_group_counts_only_its_own_requests()
    {
        var time = new FakeTimeProvider();
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new NtpServerPlugin(new NtpServerOptions { Time = time });
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("ntp", core.ToolArea);
        Assert.Equal("status", Assert.Single(core.Tools).Name);
        Assert.Equal(UserRole.Operator, core.RequiredRole(NtpTools.Status.Method));
        Assert.False(core.IsAudited(NtpTools.Status.Method));
        Assert.False(core.RequiresAllDevices(NtpTools.Status.Method));

        var key = ctx.FakeScopes.Add("Group", [inside.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "tech2", key);
        var admin = ctx.CallAsAllDevices();
        var log = plugin.Service!.Log;
        void Request(string address) => log.Add(time.GetUtcNow().UtcDateTime, ClientAddress.FromIPAddress(IPAddress.Parse(address)), 1, RequestResult.Answered);
        Request("10.0.0.49");
        Request("10.0.0.48");
        Request("10.0.0.49");

        var all = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, NtpTools.Status.Method, null))!)!;
        Assert.False(all["enabled"]!.GetValue<bool>());
        Assert.Equal("neutral", all["health"]!.GetValue<string>());
        Assert.True(all["port"]!.GetValue<int>() > 0);
        Assert.Null(all["upstream"]);
        Assert.Equal(3, all["recent_answered"]!.GetValue<int>());
        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, NtpTools.Status.Method, null))!)!;
        Assert.Equal(1, mine["recent_answered"]!.GetValue<int>());
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new Testing.FakeCoreContext();
        await using var plugin = new NtpServerPlugin(new NtpServerOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, NtpServerMethods.GetState, null);
        var before = Stored(ctx);

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: every tool is a read
        foreach (var tool in core.Tools)
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, null));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, "{}"));
        }

        Assert.Equal(before, Stored(ctx));
    }

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(Testing.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;
}
