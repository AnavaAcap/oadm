using System.Text.Json.Nodes;

using Oadm.Plugins.ActionEngine;
using Oadm.Plugins.ActionRules.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The tool of the Action Rule Editor (docs/spec/mcp.md "Plugins"): the rules the editor read last, of the caller's scope, paged; it reads no device.</summary>
public sealed class ToolTests
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";
    private const string VirtualInputTopic = "tns1:Device/tnsaxis:IO/VirtualInput";

    [Fact]
    public async Task The_rules_of_the_last_read_are_summed_up_per_name_and_paged_per_device()
    {
        var devices = new FakeDevices();
        var cameras = new FakeCameras();
        var a = new FakeDevice { Address = "10.0.0.1" };
        var b = new FakeDevice { Address = "10.0.0.2" };
        devices.Items.Add(a);
        devices.Items.Add(b);
        foreach (var device in new[] { a, b })
        {
            var camera = new RuleCamera("os12");
            camera.Configs["1"] = new RuleCamera.Config("Door action", Tcp, []);
            camera.Rules["1"] = new RuleCamera.Rule("Door", true, new EventFilter(VirtualInputTopic, null), [], null, "1");
            camera.Configs["2"] = new RuleCamera.Config("Spare action", Tcp, []);
            camera.Rules["2"] = new RuleCamera.Rule("Spare", false, null, [], null, "2");
            cameras.Cameras[device.Id] = camera;
        }

        await using var plugin = new ActionRulesPlugin();
        await plugin.StartAsync(new FakeContext(devices, cameras, new RecordingRunner(), new RecordingEvents(), null), CancellationToken.None);
        ICorePlugin core = plugin;
        Assert.Equal("device_rules", core.ToolArea);
        Assert.Equal("list", Assert.Single(core.Tools).Name);
        Assert.Equal(PermissionKeys.Show, core.RequiredPermission(ActionRuleTools.Rules.Method));
        Assert.False(core.IsAudited(ActionRuleTools.Rules.Method));

        async Task<JsonNode> ToolAsync(string? payload = null) => JsonNode.Parse((await plugin.InvokeAsync(ActionRuleTools.Rules.Method, payload, CancellationToken.None))!)!;

        var before = await ToolAsync();
        Assert.Equal(0, before["total"]!.GetValue<int>());
        Assert.Null(before["read_utc"]);

        await plugin.InvokeAsync(ActionRulesMethods.Read, null, CancellationToken.None);
        await plugin.Sweep!.Current;
        var requests = cameras.Cameras.Values.Cast<RuleCamera>().Sum(c => c.Requests.Count);

        var answer = await ToolAsync();
        Assert.Equal(4, answer["total"]!.GetValue<int>());
        Assert.Equal(2, answer["devices"]!.GetValue<int>());
        var door = answer["names"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "Door")!;
        Assert.Equal((2, 2), (door["devices"]!.GetValue<int>(), door["enabled"]!.GetValue<int>()));
        Assert.Equal(0, answer["names"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "Spare")!["enabled"]!.GetValue<int>());

        var seen = new List<string>();
        string? after = null;
        for (var page = 0; page < 5; page++)
        {
            var payload = new JsonObject { ["limit"] = 3 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var rows = await ToolAsync(payload.ToJsonString());
            seen.AddRange(rows["rules"]!.AsArray().Select(r => r!["device_id"]!.GetValue<string>() + "/" + r["rule_id"]!.GetValue<string>()));
            after = rows["next"]?.GetValue<string>();
            if (after is null)
            {
                break;
            }
        }

        Assert.Equal(4, seen.Distinct().Count());
        Assert.Equal(2, (await ToolAsync("""{"enabled":true}"""))["total"]!.GetValue<int>());
        Assert.Equal(2, (await ToolAsync($$"""{"device_id":"{{a.Id}}"}"""))["total"]!.GetValue<int>());
        Assert.Equal(requests, cameras.Cameras.Values.Cast<RuleCamera>().Sum(c => c.Requests.Count)); // the tool read no device
        var bad = await ToolAsync("""{"device_id":"x"}""");
        Assert.Equal("device_id", Assert.Single(bad["errors"]!.AsArray())!["field"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_group_gets_the_table_of_its_own_devices_only()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new ActionRulesPlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) });
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), ActionRulesMethods.Read, null);
        await plugin.Sweep!.Current;

        var mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, ActionRuleTools.Rules.Method, null))!)!;
        Assert.Equal(0, mine["devices"]!.GetValue<int>()); // the group's own table was never read
        await ctx.InvokeAsync(plugin, group, ActionRulesMethods.Read, null);
        await plugin.Sweeps!.For(key)!.Current;
        mine = JsonNode.Parse((await ctx.InvokeAsync(plugin, group, ActionRuleTools.Rules.Method, null))!)!;
        Assert.Equal(1, mine["devices"]!.GetValue<int>());
        Assert.Equal(2, JsonNode.Parse((await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), ActionRuleTools.Rules.Method, null))!)!["devices"]!.GetValue<int>());
    }
}
