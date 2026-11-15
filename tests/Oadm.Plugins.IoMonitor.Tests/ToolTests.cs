using System.Text.Json.Nodes;

using Oadm.Plugins.IoMonitor.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.IoMonitor.Tests;

/// <summary>The tool of the I/O Monitor (docs/spec/mcp.md "Plugins"): one read of one device's ports, the caller's devices only, no stream.</summary>
public sealed class ToolTests
{
    private const string Ports = """{"apiVersion":"1.0","method":"getPorts","data":{"numberOfPorts":2,"items":[{"port":"0","configurable":true,"usage":"Door","name":"Door","direction":"input","state":"closed","normalState":"open"},{"port":"1","configurable":false,"usage":"","name":"Relay","direction":"output","state":"open","normalState":"open"}]}}""";

    private static Testing.FakeDevice IoDevice() => new() { Address = "10.0.0.20", Apis = [new DeviceApi(IoMonitorPluginInfo.ApiId, "1.0")] };

    private static IoMonitorPlugin Plugin() => new(new IoMonitorOptions { RequestTimeout = TimeSpan.FromSeconds(30) });

    [Fact]
    public async Task The_ports_are_read_once_with_their_states_and_no_stream_starts()
    {
        var ctx = new Testing.FakeCoreContext();
        var device = ctx.Add(IoDevice());
        ctx.Cameras[device.Id].Respond = (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Ports) };
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("io", core.ToolArea);
        Assert.Equal("ports", Assert.Single(core.Tools).Name);
        Assert.False(core.IsAudited(IoMonitorTools.Ports.Method));

        var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), IoMonitorTools.Ports.Method, $$"""{"device_id":"{{device.Id}}"}"""))!)!;
        Assert.Null(answer["error"]);
        Assert.Equal((1, 1, 1), (answer["inputs"]!.GetValue<int>(), answer["outputs"]!.GetValue<int>(), answer["active"]!.GetValue<int>()));
        var door = answer["ports"]![0]!;
        Assert.Equal(("0", "Door", "input", true), (door["port"]!.GetValue<string>(), door["name"]!.GetValue<string>(), door["direction"]!.GetValue<string>(), door["active"]!.GetValue<bool>()));
        Assert.Equal("output", answer["ports"]![1]!["direction"]!.GetValue<string>());
        Assert.NotNull(answer["read_utc"]);
        Assert.Equal(0, plugin.StreamCount);
        Assert.Equal(0, plugin.DeviceCount);
        Assert.Single(ctx.Cameras[device.Id].Requests, r => r.StartsWith("POST " + IoMonitorPluginInfo.Path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_without_ports_or_that_cannot_be_read_answers_why_and_a_missing_id_is_a_field_error()
    {
        var ctx = new Testing.FakeCoreContext();
        var plain = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.21" });
        var locked = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.22", Status = DeviceStatus.CredentialsRequired, Apis = [new DeviceApi(IoMonitorPluginInfo.ApiId, "1.0")] });
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var admin = ctx.CallAsAllDevices();

        var none = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, IoMonitorTools.Ports.Method, $$"""{"device_id":"{{plain.Id}}"}"""))!)!;
        Assert.Equal(IoMonitorTools.NoPorts, none["error"]!.GetValue<string>());
        Assert.Empty(none["ports"]!.AsArray());
        var refused = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, IoMonitorTools.Ports.Method, $$"""{"device_id":"{{locked.Id}}"}"""))!)!;
        Assert.Equal(DeviceMessages.CredentialsRequired, refused["error"]!.GetValue<string>());
        Assert.Empty(ctx.ClientsCreated);

        var missing = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, IoMonitorTools.Ports.Method, "{}"))!)!;
        Assert.Equal("device_id", Assert.Single(missing["errors"]!.AsArray())!["field"]!.GetValue<string>());
        var unknown = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, IoMonitorTools.Ports.Method, $$"""{"device_id":"{{Guid.NewGuid()}}"}"""))!)!;
        Assert.Equal("device_id", Assert.Single(unknown["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tool_reaches_no_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(IoDevice());
        var outside = ctx.Add(IoDevice());
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(IoMonitorTools.Ports.Method, """{"device_id":"{device}"}""")
            .RunAsync();
    }
}
