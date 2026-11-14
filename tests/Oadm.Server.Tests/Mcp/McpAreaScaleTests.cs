using System.Text;
using System.Text.Json.Nodes;

using Oadm.Server.Tests.Perf;
using Oadm.Server.Tools;

using Xunit.Abstractions;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The tools of the areas at the device limit (docs/spec/mcp.md "Tests"): 100,000 devices. Tagging every device of a filter is a
/// preview that counts without listing, then one run in one transaction; the compliance results for a filter and the tag list
/// stay within their budgets, and no answer is larger than 32 KB. The measured times are in the test output.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class McpAreaScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TaggingAndComplianceAt100000DevicesStayFastAndUnder32Kb()
    {
        await using var server = await McpTestServer.StartAsync();
        await ServerScale.SeedDevicesAsync(server.Host);
        var key = await server.CreateKeyAsync();

        JsonObject answer = [];
        async Task<JsonObject> MeasureAsync(string what, TimeSpan budget, string tool, JsonObject? arguments = null)
        {
            await ServerScale.MeasureAsync(output, what, budget, async () =>
            {
                var result = await server.CallToolAsync(key, tool, arguments);
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                var text = result["content"]![0]!["text"]!.GetValue<string>();
                var bytes = Encoding.UTF8.GetByteCount(text);
                output.WriteLine($"  {tool}: {bytes:N0} bytes");
                Assert.True(bytes <= ToolAnswers.MaxBytes, $"{tool} answered {bytes:N0} bytes");
                answer = result["structuredContent"]!.AsObject();
            });
            return answer;
        }

        static JsonObject Unreachable() => new() { ["status"] = new JsonArray("Unreachable") };
        var preview = await MeasureAsync("oadm_tags_assign_preview of 100,000 devices", TimeSpan.FromSeconds(60), "oadm_tags_assign_preview", new JsonObject { ["add"] = new JsonArray("Perf") });
        Assert.Equal(ServerScale.Devices, preview["will_change"]!.GetValue<int>());
        Assert.Equal(TagTools.ShownDevices, preview["first"]!.AsArray().Count);
        Assert.Equal(ServerScale.Devices, (await MeasureAsync("oadm_tags_assign_run on 100,000 devices", TimeSpan.FromSeconds(120), "oadm_tags_assign_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() }))["devices_changed"]!.GetValue<int>());

        var list = await MeasureAsync("oadm_tags_list with 100,000 tagged devices", TimeSpan.FromSeconds(30), "oadm_tags_list");
        Assert.Equal(ServerScale.Devices, list["tags"]![0]!["devices"]!.GetValue<int>());

        var partial = await MeasureAsync("oadm_tags_assign_preview with a filter (status Unreachable, remove)", TimeSpan.FromSeconds(60), "oadm_tags_assign_preview", new JsonObject { ["filter"] = Unreachable(), ["remove"] = new JsonArray("Perf") });
        Assert.Equal(ServerScale.Devices / 50, partial["will_change"]!.GetValue<int>());

        var summary = await MeasureAsync("oadm_compliance_summary for a filter of 100,000 devices", TimeSpan.FromSeconds(60), "oadm_compliance_summary", new JsonObject { ["filter"] = Unreachable() });
        Assert.Equal(ServerScale.Devices / 50, summary["devices"]!.GetValue<int>());
        await MeasureAsync("oadm_compliance_checks", TimeSpan.FromSeconds(10), "oadm_compliance_checks");
        await MeasureAsync("oadm_rules_list", TimeSpan.FromSeconds(10), "oadm_rules_list");
        await MeasureAsync("oadm_events_log, 200 entries", TimeSpan.FromSeconds(10), "oadm_events_log", new JsonObject { ["limit"] = ToolAnswers.MaxRows });
        await MeasureAsync("oadm_sites_list", TimeSpan.FromSeconds(10), "oadm_sites_list");
    }
}
