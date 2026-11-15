using System.Text.Json.Nodes;

using Oadm.Plugins.MetadataMonitor.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>The tool of the Metadata Monitor (docs/spec/mcp.md "Plugins"): the open streams of the caller's devices, from memory, read only.</summary>
public sealed class ToolTests
{
    private static async Task<JsonNode> StreamsAsync(MetadataMonitorPlugin plugin, ICallContext caller, string? payload = null) =>
        JsonNode.Parse((await plugin.InvokeAsync(caller, MetadataTools.Streams.Method, payload, CancellationToken.None))!)!;

    [Fact]
    public async Task The_open_streams_show_their_state_counts_and_last_messages_and_the_tool_opens_none()
    {
        await using var rig = await new Rig().StartAsync();
        ICorePlugin core = rig.Plugin;
        Assert.Equal("metadata", core.ToolArea);
        Assert.Equal("streams", Assert.Single(core.Tools).Name);
        Assert.False(core.IsAudited(MetadataTools.Streams.Method));

        var none = await StreamsAsync(rig.Plugin, rig.Call);
        Assert.Equal(MetadataTools.NoStreams, none["note"]!.GetValue<string>());
        Assert.Equal(0, rig.Streams.Attempts);

        var reply = await rig.StartStreamAsync();
        Assert.NotNull(reply.StreamId);
        for (var i = 1; i <= 5; i++)
        {
            rig.Streams.Last!.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", i, i % 2));
        }

        await Wait.UntilAsync(() => rig.Streams.Last!.Consumed == 5);
        var answer = await StreamsAsync(rig.Plugin, rig.Call);
        var stream = Assert.Single(answer["streams"]!.AsArray())!;
        Assert.Equal(rig.Camera.Id.ToString(), stream["device_id"]!.GetValue<string>());
        Assert.Equal(5, stream["messages"]!.GetValue<long>());
        Assert.Equal(MetadataTools.RecentInList, stream["recent"]!.AsArray().Count);
        Assert.Contains("VirtualInput", stream["recent"]![0]!["topic"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotNull(stream["last_message"]);

        var one = await StreamsAsync(rig.Plugin, rig.Call, $$"""{"device_id":"{{rig.Camera.Id}}"}""");
        Assert.Equal(5, one["streams"]![0]!["recent"]!.AsArray().Count); // more of them for one device
        Assert.Equal(1, rig.Streams.Attempts); // the tool opened no stream
        await rig.StopStreamAsync(reply.StreamId!);
        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 0);
        Assert.Equal(0, (await StreamsAsync(rig.Plugin, rig.Call))["total"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_group_sees_only_the_streams_of_its_devices()
    {
        await using var rig = await new Rig().StartAsync();
        var other = new TestDevice("10.0.0.49", "P3265-V");
        rig.Devices.Items.Add(other);
        var group = Testing.FakeCallContext.OverGroup([other.Id], rig.Devices, eventStreams: rig.Streams, userName: "tech2");
        Assert.NotNull((await rig.StartStreamAsync()).StreamId); // the admin's page on the first camera

        Assert.Equal(0, (await StreamsAsync(rig.Plugin, group))["total"]!.GetValue<int>());
        Assert.Equal(1, (await StreamsAsync(rig.Plugin, rig.Call))["total"]!.GetValue<int>());
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => StreamsAsync(rig.Plugin, group, $$"""{"device_id":"{{rig.Camera.Id}}"}"""));
    }

    [Fact]
    public async Task The_tool_reaches_no_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        await using var plugin = new MetadataMonitorPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(MetadataTools.Streams.Method, """{"device_id":"{device}"}""")
            .RunAsync();
    }
}
