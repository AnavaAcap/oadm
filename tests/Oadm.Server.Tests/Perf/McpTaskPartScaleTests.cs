using System.Text;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Plugins.Firmware;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;
using Oadm.Server.Tests.Mcp;
using Oadm.Server.Tools;

using Xunit.Abstractions;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// The task tools that read many devices or tasks (docs/spec/mcp.md "Tests"): the preview of Upgrade AXIS OS plans 100,000
/// devices from the cached model and version, and the preview of a cancel reads 50,000 tasks; the answers stay under 32 KB.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class McpTaskPartScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThePreviewOfAFirmwareUpgradePlans100000DevicesAndACancelPreviewReads50000TasksUnder32Kb()
    {
        await using var server = await McpTestServer.StartAsync();
        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(new FirmwareTaskPlugin(), new PluginOrigin(FirmwareTaskPlugin.PluginId, "1.0.0", null)));
        var fwmgr = new List<DeviceApi> { new("fwmgr", "1.0") };
        await using (var db = await server.Host.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var ids = new List<Guid>(ServerScale.Devices);
            for (var i = 0; i < ServerScale.Devices; i++)
            {
                var id = Guid.NewGuid();
                ids.Add(id);
                db.Devices.Add(new Device
                {
                    Id = id,
                    Serial = $"ACCC8E{i:X6}",
                    Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                    Scheme = DeviceScheme.Http,
                    Status = i % 50 == 0 ? SdkDeviceStatus.Unreachable : SdkDeviceStatus.Ok,
                    Model = i % 2 == 0 ? "P3265-V" : "M3085-V",
                    FirmwareVersion = i % 10 == 0 ? "12.11.77" : "12.8.0",
                    Category = DeviceCategory.Camera,
                    Apis = fwmgr, // shared between devices, like the server's own list
                });
            }

            await db.SaveChangesAsync();
            var batch = Guid.NewGuid();
            var start = DateTimeOffset.UtcNow.AddDays(-1);
            await server.Host.Get<EfTaskStore>().AddRangeAsync([.. Enumerable.Range(0, ServerScale.Tasks).Select(i => new TaskRecord(
                Guid.NewGuid(), "perf.task0", "Perf task", TaskState.Done, "tester", start.AddSeconds(i), start.AddSeconds(i), start.AddSeconds(i + 1), 100, null,
                [new TaskDeviceRecord(ids[i % ids.Count], TaskState.Done, null, 100)], batch))], CancellationToken.None);
            var key = await server.CreateKeyAsync();

            JsonObject answer = [];
            async Task MeasureAsync(string what, TimeSpan budget, string tool, JsonObject arguments)
            {
                await ServerScale.MeasureAsync(output, what, budget, async () =>
                {
                    var result = await server.CallToolAsync(key, tool, arguments);
                    Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                    var bytes = Encoding.UTF8.GetByteCount(result["content"]![0]!["text"]!.GetValue<string>());
                    output.WriteLine($"  {tool}: {bytes:N0} bytes");
                    Assert.True(bytes <= ToolAnswers.MaxBytes, $"{tool} answered {bytes:N0} bytes");
                    answer = result["structuredContent"]!.AsObject();
                });
            }

            // Half the devices are another model, a tenth is up to date, every 50th is unreachable (and some are both).
            await MeasureAsync("oadm_task_preview of Upgrade AXIS OS on 100,000 devices", TimeSpan.FromSeconds(60), "oadm_task_preview", new JsonObject
            {
                ["task"] = FirmwareTaskPlugin.PluginId,
                ["filter"] = new JsonObject(),
                ["input"] = new JsonObject { ["files"] = new JsonArray(new JsonObject { ["fileId"] = "0123456789abcdef0123456789abcdef", ["fileName"] = "P3265-V_12_11_77.bin" }) },
            });
            Assert.Equal(ServerScale.Devices, answer["devices"]!.GetValue<int>());
            var expected = Enumerable.Range(0, ServerScale.Devices).Count(i => i % 2 == 0 && i % 10 != 0 && i % 50 != 0);
            Assert.Equal(expected, answer["will_run"]!.GetValue<int>());
            Assert.True(answer["skipped"]!.AsArray().Count <= TaskRunTools.ShownReasons + 1);

            await MeasureAsync("oadm_tasks_cancel_preview of a run of 50,000 finished tasks", TimeSpan.FromSeconds(30), "oadm_tasks_cancel_preview", new JsonObject { ["batch"] = batch.ToString() });
            Assert.Equal(ServerScale.Tasks, answer["matched"]!.GetValue<int>());
            Assert.Equal(0, answer["will_cancel"]!.GetValue<int>());
            Assert.Null(answer["handle"]);
            Assert.Equal(ServerScale.Tasks, Assert.Single(answer["cannot"]!.AsArray())!["count"]!.GetValue<int>());
        }
    }
}
