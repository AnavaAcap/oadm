using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Reading;
using Oadm.Plugins.AppManagement.Tools;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>
/// The tool of Application management (docs/spec/mcp.md "Plugins"): what the page read last in the caller's session, of the
/// caller's devices, summed up per application and paged per device and application; it reads no device.
/// </summary>
public sealed class ToolTests : IAsyncLifetime
{
    private readonly FakeCoreContext _ctx = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero));
    private AppManagementPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new AppManagementPlugin(new AppManagementOptions { Time = _time, EventInterval = TimeSpan.FromHours(1) });
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private static readonly InstalledApplication Vmd = Samples.App("vmd", "AXIS Video Motion Detection", "4.5.2", bundled: true);
    private static readonly InstalledApplication Lpv = Samples.App("lpv", "AXIS License Plate Verifier", "2.10", "Stopped", "Missing");

    private async Task ReadAsync(ICallContext call)
    {
        await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.Read, AppManagementJson.Serialize(new ReadRequest { All = true }));
        await Eventually.TrueAsync(async () => !AppManagementJson.Deserialize<ReadState>(await _ctx.InvokeAsync(_plugin, call, AppManagementMethods.GetState, null)).Reading, "the read ended");
    }

    private async Task<JsonNode> ToolAsync(ICallContext call, string? payload = null) =>
        JsonNode.Parse((await _ctx.InvokeAsync(_plugin, call, AppTools.Applications.Method, payload))!)!;

    [Fact]
    public async Task The_applications_are_summed_up_per_application_and_paged_per_device_without_a_read()
    {
        ICorePlugin core = _plugin;
        Assert.Equal("applications", Assert.Single(core.Tools).Name);
        Assert.Equal(AppManagementInfo.ViewPermission, core.RequiredPermission(AppTools.Applications.Method));
        Assert.Equal(UserRole.Operator, core.RequiredRole(AppTools.Applications.Method));
        var devices = Enumerable.Range(1, 3).Select(i => _ctx.Add(Samples.ManagedDevice($"10.0.0.{i}"), Samples.Camera(Vmd, Lpv))).ToList();
        var call = _ctx.CallAsAllDevices();

        var before = await ToolAsync(call);
        Assert.Equal(0, before["total"]!.GetValue<int>());
        Assert.Null(before["read_utc"]);
        Assert.All(_ctx.Cameras.Values, c => Assert.Empty(c.Requests)); // the tool reads nothing itself

        await ReadAsync(call);
        var requests = _ctx.Cameras.Values.Sum(c => c.Requests.Count);
        var answer = await ToolAsync(call);
        Assert.Equal(6, answer["total"]!.GetValue<int>());
        Assert.Equal(3, answer["devices"]!.GetValue<int>());
        var lpv = answer["applications"]!.AsArray().Single(a => a!["name"]!.GetValue<string>() == "lpv")!;
        Assert.Equal(3, lpv["devices"]!.GetValue<int>());
        Assert.Equal(0, lpv["running"]!.GetValue<int>());
        Assert.Equal(3, lpv["license_problems"]!.GetValue<int>());
        Assert.Equal(["2.10"], lpv["versions"]!.AsArray().Select(v => v!.GetValue<string>()));

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 8; page++)
        {
            var payload = new JsonObject { ["limit"] = 4 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var rows = await ToolAsync(call, payload.ToJsonString());
            seen.AddRange(rows["rows"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>() + "/" + r["application"]!.GetValue<string>()));
            after = rows["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(6, seen.Distinct().Count());
        Assert.Equal(devices.Count, seen.Select(s => s.Split('/')[0]).Distinct().Count());
        Assert.Equal(requests, _ctx.Cameras.Values.Sum(c => c.Requests.Count)); // still no read of its own

        Assert.Equal(3, (await ToolAsync(call, """{"application":"PLATE"}"""))["total"]!.GetValue<int>());
        Assert.Equal(3, (await ToolAsync(call, """{"status":"stopped"}"""))["total"]!.GetValue<int>());
        var bad = await ToolAsync(call, """{"status":"sleeping"}""");
        Assert.Equal("status", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_user_gets_only_the_rows_of_its_devices_and_another_session_none()
    {
        var inside = _ctx.Add(Samples.ManagedDevice("10.0.0.1"), Samples.Camera(Vmd));
        var outside = _ctx.Add(Samples.ManagedDevice("10.0.0.2"), Samples.Camera(Vmd));
        var all = _ctx.CallAsAllDevices();
        await ReadAsync(all);
        Assert.Equal(2, (await ToolAsync(all))["devices"]!.GetValue<int>());

        // Another session (another login, an access key) sees nothing of this one.
        var group = _ctx.CallAsGroup([inside.Id], "tech2");
        Assert.Equal(0, (await ToolAsync(group))["total"]!.GetValue<int>());
        await ReadAsync(group);
        var mine = await ToolAsync(group);
        Assert.Equal(inside.Id.ToString(), Assert.Single(mine["rows"]!.AsArray())!["device_id"]!.GetValue<string>());
        Assert.DoesNotContain(outside.Id.ToString(), mine.ToJsonString(), StringComparison.Ordinal);
    }
}
