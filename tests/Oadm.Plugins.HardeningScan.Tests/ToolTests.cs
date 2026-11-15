using System.Diagnostics;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.HardeningScan.Scanning;
using Oadm.Plugins.HardeningScan.Tools;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.HardeningScan.Tests;

/// <summary>The tool of the Hardening scan (docs/spec/mcp.md "Plugins"): the results per device, paged, for the caller's devices only.</summary>
public sealed class ToolTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_results_are_a_summary_per_device_paged_by_device_and_level()
    {
        var ctx = new Testing.FakeCoreContext();
        var devices = Enumerable.Range(1, 5).Select(i => ctx.Add(new Testing.FakeDevice { Address = $"10.0.0.{i}" })).ToList();
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("hardening", core.ToolArea);
        Assert.Equal(["results", "scan_start", "scan_stop", "detail"], core.Tools.Select(t => t.Name));
        Assert.Equal(UserRole.Operator, core.RequiredRole(HardeningTools.Results.Method));
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(HardeningTools.Results.Method)); // starting a scan stays "scan"

        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, HardeningMethods.StartScan, """{"level":"Basic"}""");
        await plugin.Service!.WaitAsync();

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 6; page++)
        {
            var payload = new JsonObject { ["limit"] = 2 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, HardeningTools.Results.Method, payload.ToJsonString()))!)!;
            Assert.Equal(5, answer["total"]!.GetValue<int>());
            Assert.Equal("done", answer["scan"]!["state"]!.GetValue<string>());
            seen.AddRange(answer["results"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
            after = answer["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(devices.Select(d => d.Id.ToString()).Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(5, seen.Distinct().Count());

        var row = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, HardeningTools.Results.Method, """{"level":"basic","limit":1}"""))!)!["results"]![0]!;
        Assert.Equal("basic", row["level"]!.GetValue<string>());
        Assert.StartsWith("10.0.0.", row["address"]!.GetValue<string>(), StringComparison.Ordinal);
        var bad = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, HardeningTools.Results.Method, """{"level":"deep"}"""))!)!;
        Assert.Equal("level", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_gets_the_results_of_its_own_devices_only()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = ctx.CallAsGroup([inside.Id], "tech2");
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, HardeningMethods.StartScan, """{"level":"Basic"}""");
        await plugin.Service!.WaitAsync();

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, HardeningTools.Results.Method, null))!)!;
        Assert.Equal([inside.Id.ToString()], mine["results"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>()));
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, HardeningTools.Results.Method, null))!)!["total"]!.GetValue<int>());
        Assert.Empty(ctx.RefusedAccesses);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_hundred_thousand_devices_page_fast_and_every_page_stays_within_the_limit()
    {
        var count = Oadm.Sdk.Devices.DeviceLimits.MaxDevices;
        var devices = Enumerable.Range(0, count).Select(i => (Oadm.Sdk.Devices.IDeviceInfo)new Testing.FakeDevice { Address = $"10.{i / 65536 % 256}.{i / 256 % 256}.{i % 256}" }).ToList();
        var now = DateTimeOffset.UtcNow;
        var results = devices.Select((d, i) => new DeviceResult { DeviceId = d.Id, Level = ScanLevel.Basic, ScannedUtc = now, States = i % 3 == 0 ? "ppfpwp" : "pppppp" }).ToList();
        var state = new HardeningState { Results = results };

        var watch = Stopwatch.StartNew();
        string? after = null;
        var rows = 0;
        var slowest = TimeSpan.Zero;
        for (var page = 0; page < 5; page++)
        {
            var started = watch.Elapsed;
            var payload = new JsonObject { ["limit"] = 200, ["has"] = "fail" };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var json = HardeningTools.Answer(state, devices, payload.ToJsonString());
            slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, (watch.Elapsed - started).Ticks));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 32 * 1024, "an answer within 32 KB");
            var answer = JsonNode.Parse(json)!;
            rows += answer["results"]!.AsArray().Count;
            after = answer["next"]?.GetValue<string>();
        }

        output.WriteLine($"{count:N0} devices: {rows} rows in 5 pages, slowest page {slowest.TotalMilliseconds:F0} ms");
        Assert.True(rows > 0);
        Assert.True(slowest < TimeSpan.FromSeconds(2), $"too slow: {slowest.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Every_tool_without_Changes_leaves_the_stored_state_as_it_was()
    {
        var ctx = new Testing.FakeCoreContext();
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.7" });
        await using var plugin = new HardeningScanPlugin(new HardeningScanOptions { Time = new FakeTimeProvider() });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();
        await ctx.InvokeAsync(plugin, admin, HardeningMethods.StartScan, """{"level":"Basic"}""");
        await plugin.Service!.WaitAsync();
        var before = Stored(ctx);
        Assert.NotEmpty(ctx.PluginSettings.Values); // the scan stored its results: there is something to compare

        ICorePlugin core = plugin;
        Assert.All(core.Tools, t => Assert.False(t.Changes)); // no change tool here: the page starts a scan without asking
        foreach (var tool in core.Tools.Where(t => t.Name is not ("scan_start" or "scan_stop"))) // the page's starts: PageToolTests
        {
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, null));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"level":"basic","limit":1}"""));
            Assert.NotNull(await ctx.InvokeAsync(plugin, admin, tool.Method, """{"level":"deep"}""")); // an invalid call too
        }

        Assert.Equal(before, Stored(ctx));
    }

    /// <summary>Everything the plugin keeps: its settings and the number of record writes.</summary>
    private static string Stored(Testing.FakeCoreContext ctx) =>
        string.Join('\n', ctx.PluginSettings.Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value)) + "\nrecord writes: " + ctx.PluginRecords.WrittenRecords;
}
