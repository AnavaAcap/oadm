using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Persistence;
using Oadm.Core.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Mcp;

using Xunit.Abstractions;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// The MCP tools at the device limit (docs/spec/mcp.md "Tests"): 100,000 devices with 100 APIs each and 50,000 tasks; the
/// summary, search pages, one device, the task list and the server info stay within their budgets, and no answer of any tool
/// is larger than 32 KB.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class McpScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryToolAt100000DevicesAnd50000TasksStaysFastAndUnder32Kb()
    {
        await using var server = await McpTestServer.StartAsync();
        var ids = await ServerScale.SeedDevicesAsync(server.Host);
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var tasks = Enumerable.Range(0, ServerScale.Tasks).Select(i => new TaskRecord(
            Guid.NewGuid(), "perf.task0", "Perf task", TaskState.Done, "tester", start.AddSeconds(i), start.AddSeconds(i), start.AddSeconds(i + 1), 100, null,
            [new TaskDeviceRecord(ids[i % ids.Count], TaskState.Done, null, 100)])
        {
            Steps = [new TaskStepInfo(0, "Do it", TaskStepState.Done, null, 100, start.AddSeconds(i), start.AddSeconds(i + 1))],
        }).ToList();
        await server.Host.Get<EfTaskStore>().AddRangeAsync(tasks, CancellationToken.None);
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
                Assert.True(bytes <= McpAnswers.MaxBytes, $"{tool} answered {bytes:N0} bytes");
                Assert.True(Encoding.UTF8.GetByteCount(result["structuredContent"]!.ToJsonString()) <= McpAnswers.MaxBytes);
                answer = result["structuredContent"]!.AsObject();
            });
            return answer;
        }

        var info = await MeasureAsync("oadm_server_info at 100,000 devices", TimeSpan.FromSeconds(30), "oadm_server_info");
        Assert.Equal(ServerScale.Devices, info["devices"]!["count"]!.GetValue<int>());

        var summary = await MeasureAsync("oadm_devices_summary by status of 100,000 devices", TimeSpan.FromSeconds(30), "oadm_devices_summary");
        Assert.Equal(ServerScale.Devices, summary["total"]!.GetValue<int>());
        Assert.Equal(ServerScale.Devices / 50, summary["groups"]!.AsArray().Single(g => g!["value"]!.GetValue<string>() == "Unreachable")!["count"]!.GetValue<int>());

        var page = await MeasureAsync("oadm_devices_search, first page of 200 with every field", TimeSpan.FromSeconds(30), "oadm_devices_search", new JsonObject
        {
            ["limit"] = McpAnswers.MaxRows,
            ["fields"] = new JsonArray([.. DeviceTools.ExtraFields.Select(f => (JsonNode)f)]),
        });
        Assert.Equal(ServerScale.Devices, page["total"]!.GetValue<int>());
        Assert.NotNull(page["next_cursor"]);
        await MeasureAsync("oadm_devices_search, the next page", TimeSpan.FromSeconds(30), "oadm_devices_search", new JsonObject
        {
            ["limit"] = McpAnswers.MaxRows,
            ["fields"] = new JsonArray([.. DeviceTools.ExtraFields.Select(f => (JsonNode)f)]),
            ["cursor"] = page["next_cursor"]!.GetValue<string>(),
        });

        var filtered = await MeasureAsync("oadm_devices_search with a filter (status Unreachable, sorted by host name)", TimeSpan.FromSeconds(30), "oadm_devices_search", new JsonObject
        {
            ["filter"] = new JsonObject { ["status"] = new JsonArray("Unreachable"), ["search"] = "10." },
            ["sort"] = "-host_name",
        });
        Assert.Equal(ServerScale.Devices / 50, filtered["total"]!.GetValue<int>());

        await MeasureAsync("oadm_devices_get of one device with 100 APIs", TimeSpan.FromSeconds(30), "oadm_devices_get", new JsonObject { ["device_id"] = ids[^1].ToString() });

        var taskList = await MeasureAsync("oadm_tasks_list of 50,000 tasks with the counts", TimeSpan.FromSeconds(15), "oadm_tasks_list", new JsonObject { ["limit"] = McpAnswers.MaxRows });
        Assert.Equal(ServerScale.Tasks, taskList["counts"]!["Done"]!.GetValue<int>());

        await MeasureAsync("oadm_tasks_get of one task", TimeSpan.FromSeconds(15), "oadm_tasks_get", new JsonObject { ["task_id"] = tasks[^1].Id.ToString() });
        await MeasureAsync("oadm_logs_search, 200 lines", TimeSpan.FromSeconds(5), "oadm_logs_search", new JsonObject { ["limit"] = McpAnswers.MaxRows });
        await MeasureAsync("oadm_logs_audit, 200 entries", TimeSpan.FromSeconds(5), "oadm_logs_audit", new JsonObject { ["limit"] = McpAnswers.MaxRows });
    }
}
