using System.Text;
using System.Text.Json.Nodes;

using Oadm.Server.Tests.Mcp;
using Oadm.Server.Tools;

using Xunit.Abstractions;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// The change tools of the devices part at the device limit (docs/spec/mcp.md "Tests"): 100,000 devices. The previews of
/// Refresh, Remove and Set credentials resolve the filter to the fixed list of 100,000 devices in one call and answer at most
/// 20 rows, and the Remove run takes the whole list in one call; no answer is larger than 32 KB.
/// </summary>
[Trait("Category", "Perf")]
[Collection(ServerScale.OneAtATime)]
public sealed class McpDeviceChangeScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ThePreviewsAndTheRemoveRunAt100000DevicesStayFastAndUnder32Kb()
    {
        await using var server = await McpTestServer.StartAsync();
        await ServerScale.SeedDevicesAsync(server.Host);
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "installer", Password = "Install-pw-1" });
        var key = await server.CreateKeyAsync();
        static JsonObject Everything() => new() { ["models"] = new JsonArray("P3265-V") };

        JsonObject answer = [];
        async Task<JsonObject> MeasureAsync(string what, TimeSpan budget, string tool, JsonObject arguments)
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

        var refresh = await MeasureAsync("oadm_devices_refresh_preview of 100,000 devices", TimeSpan.FromSeconds(60), "oadm_devices_refresh_preview", new JsonObject { ["filter"] = Everything() });
        Assert.Equal(ServerScale.Devices, refresh["devices"]!.GetValue<int>());
        Assert.Equal(DeviceChangeTools.ShownRows, refresh["first"]!.AsArray().Count);

        var credentials = await MeasureAsync("oadm_devices_credentials_preview of 100,000 devices", TimeSpan.FromSeconds(60), "oadm_devices_credentials_preview",
            new JsonObject { ["filter"] = Everything(), ["credential"] = "installer" });
        Assert.Equal(ServerScale.Devices, credentials["devices"]!.GetValue<int>());

        var remove = await MeasureAsync("oadm_devices_remove_preview of 100,000 devices", TimeSpan.FromSeconds(60), "oadm_devices_remove_preview", new JsonObject { ["filter"] = Everything() });
        Assert.Equal(ServerScale.Devices, remove["devices"]!.GetValue<int>());

        var run = await MeasureAsync("oadm_devices_remove_run of 100,000 devices", TimeSpan.FromSeconds(180), "oadm_devices_remove_run", new JsonObject { ["handle"] = remove["handle"]!.GetValue<string>() });
        Assert.Equal(ServerScale.Devices, run["devices"]!.GetValue<int>());
        Assert.Equal(0, (await server.Host.Devices.SearchAsync(new Proto.SearchDevicesRequest())).Total);
    }
}
