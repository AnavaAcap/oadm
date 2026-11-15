using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceTriggers.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>The tools of Device triggers (docs/spec/mcp.md "Plugins"): the page's list and one trigger's devices, of the caller's scope only.</summary>
public sealed class ToolTests
{
    private static Runtime.DeviceTriggersOptions Options() => new() { ResolveInterval = TimeSpan.FromHours(1) };

    private static async Task<TriggerDefinition> SaveAsync(Testing.FakeCoreContext ctx, DeviceTriggersPlugin plugin, ICallContext call, TriggerDefinition trigger)
    {
        var reply = TriggerJson.Deserialize<SaveReply>(await ctx.InvokeAsync(plugin, call, DeviceTriggersMethods.Save, TriggerJson.Serialize(new SaveRequest(trigger))));
        return reply.Trigger ?? throw new InvalidOperationException(reply.Error);
    }

    [Fact]
    public async Task Each_group_lists_only_its_triggers_and_their_devices()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = ctx.FakeScopes.Add("Building B", [b.Id]);
        await using var plugin = new DeviceTriggersPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal(["list", "devices"], core.Tools.Select(t => t.Name));
        Assert.All(core.Tools, t => Assert.Equal(UserRole.Operator, core.RequiredRole(t.Method)));
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        var ben = Testing.FakeCallContext.WithGroup(ctx, [b.Id], "ben", keyB);
        var admin = ctx.CallAsAllDevices();
        var door = await SaveAsync(ctx, plugin, anna, new TriggerDefinition { Name = "Door", Topic = "t", EventName = "Virtual input", Enabled = false, DeviceIds = [a.Id] });
        await SaveAsync(ctx, plugin, ben, new TriggerDefinition { Name = "Gate", Topic = "t", Enabled = false, DeviceIds = [b.Id] });

        async Task<JsonNode> ToolAsync(ICallContext call, PluginTool tool, string? payload = null) =>
            JsonNode.Parse((await ctx.InvokeAsync(plugin, call, tool.Method, payload))!)!;

        var mine = await ToolAsync(anna, TriggerTools.Triggers);
        var row = Assert.Single(mine["triggers"]!.AsArray())!;
        Assert.Equal("Door", row["name"]!.GetValue<string>());
        Assert.Equal("Virtual input", row["event"]!.GetValue<string>());
        Assert.False(row["enabled"]!.GetValue<bool>());
        Assert.Equal(1, row["devices"]!.GetValue<int>());
        Assert.Equal(2, (await ToolAsync(admin, TriggerTools.Triggers))["total"]!.GetValue<int>());
        Assert.Equal(1, (await ToolAsync(admin, TriggerTools.Triggers, """{"search":"gate"}"""))["total"]!.GetValue<int>());

        // Another group's trigger answers like one that no longer exists.
        var payload = $$"""{"trigger_id":"{{door.Id}}"}""";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, ben, TriggerTools.Devices.Method, payload));
        var devices = await ToolAsync(anna, TriggerTools.Devices, payload);
        Assert.Equal("Door", devices["trigger"]!.GetValue<string>());
        Assert.DoesNotContain(b.Id.ToString(), devices.ToJsonString(), StringComparison.Ordinal);
        Assert.Empty(ctx.RefusedAccesses);

        var bad = await ToolAsync(anna, TriggerTools.Devices, """{"trigger_id":"x"}""");
        Assert.Equal("trigger_id", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }
}
