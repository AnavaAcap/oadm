using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceTriggers.Tools;
using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DeviceTriggers.Tests;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): read the devices' events, save, turn on or off,
/// copy and delete a trigger, with the rights of the page methods they wrap; a preview writes nothing.
/// </summary>
public sealed class PageToolTests
{
    private static Runtime.DeviceTriggersOptions Options() => new() { ResolveInterval = TimeSpan.FromHours(1) };

    private static JsonNode Parse(string? json) => JsonNode.Parse(json!)!;

    private static string FirstError(JsonNode answer, out string field)
    {
        var error = answer["errors"]![0]!;
        field = error["field"]!.GetValue<string>();
        return error["error"]!.GetValue<string>();
    }

    [Fact]
    public void Each_tool_has_the_rights_of_its_page_method_and_a_preview_is_never_audited()
    {
        ICorePlugin core = new DeviceTriggersPlugin();
        var set = TriggerPageTools.Set;
        Assert.Equal(
            ["list", "devices", "events_read", "events", "save", "set_enabled", "copy", "delete"],
            core.Tools.Select(t => t.Name));
        foreach (var tool in set.Tools)
        {
            var page = set.PageMethodOf(tool.Method)!;
            Assert.Equal(core.RequiredRole(page), core.RequiredRole(tool.Method));
            Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(tool.Method));
            Assert.Equal(core.RequiresAllDevices(page), core.RequiresAllDevices(tool.Method));
            Assert.Equal(core.IsAudited(page), core.IsAudited(tool.Method));
            if (tool.Changes)
            {
                Assert.Equal(UserRole.Admin, core.RequiredRole(tool.PreviewMethod));
                Assert.False(core.IsAudited(tool.PreviewMethod));
            }
        }

        Assert.Equal(UserRole.Operator, core.RequiredRole("tool.events_read"));
        Assert.NotNull(core.SystemAccessReason("tool.save"));
        Assert.NotNull(core.SystemAccessReason("tool.set_enabled.preview"));
        Assert.True(set.ToolOf("tool.delete")!.Destructive);
        Assert.All(set.Tools.Where(t => t.Name is "save" or "set_enabled" or "copy" or "delete"), t => Assert.True(t.Changes));
    }

    [Fact]
    public async Task A_trigger_is_made_from_a_read_of_the_devices_events_and_the_preview_saves_nothing()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();

        var started = Parse(await rig.Plugin.InvokeAsync("tool.events_read", $$"""{"device_ids":["{{a.Id}}"],"devices_total":1}""", CancellationToken.None));
        var readId = started["read_id"]!.GetValue<string>();
        JsonNode events = started;
        await Wait.UntilAsync(() =>
        {
            events = Parse(rig.Plugin.InvokeAsync("tool.events", $$"""{"read_id":"{{readId}}","limit":200}""", CancellationToken.None).GetAwaiter().GetResult());
            return events["finished"]!.GetValue<bool>();
        }, "the read");
        Assert.Equal(42, events["event_count"]!.GetValue<int>());
        var virtualInput = events["events"]!.AsArray().Single(e => e!["topic"]!.GetValue<string>() == Fixture.VirtualInput)!;
        Assert.Equal("port", virtualInput["sources"]![0]!["name"]!.GetValue<string>());

        var args = $$"""{"name":"Gate open","read_id":"{{readId}}","topic":"{{Fixture.VirtualInput}}","filters":[{"name":"port","value":"3"}],"devices":["{{a.Id}}"]}""";
        var before = rig.Settings.Values.GetValueOrDefault(DeviceTriggersPlugin.SettingsKey);
        var plan = Parse(await rig.Plugin.InvokeAsync("tool.save.preview", args, CancellationToken.None));
        Assert.Equal("create", plan["change"]!.GetValue<string>());
        Assert.Equal("Port number: 3", plan["filter"]!.GetValue<string>());
        Assert.Equal((1, "trigger"), (plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
        Assert.Empty(rig.Plugin.Triggers);
        Assert.Equal(before, rig.Settings.Values.GetValueOrDefault(DeviceTriggersPlugin.SettingsKey));

        var saved = Parse(await rig.Plugin.InvokeAsync("tool.save", args, CancellationToken.None));
        var trigger = Assert.Single(rig.Plugin.Triggers);
        Assert.Equal(trigger.Id.ToString(), saved["trigger_id"]!.GetValue<string>());
        Assert.Equal([new TriggerFilter("port", "3")], trigger.Filters);
        Assert.Equal(["port"], trigger.SourceValues);
        Assert.True(trigger.Enabled);

        // A second save of the same name: the page's own check.
        var again = Parse(await rig.Plugin.InvokeAsync("tool.save.preview", args, CancellationToken.None));
        Assert.Equal("Another trigger has this name.", FirstError(again, out var field));
        Assert.Equal("name", field);

        // A change keeps what it does not name.
        var renamed = Parse(await rig.Plugin.InvokeAsync("tool.save", $$"""{"trigger_id":"{{trigger.Id}}","name":"Gate"}""", CancellationToken.None));
        Assert.Equal("Gate", renamed["name"]!.GetValue<string>());
        Assert.Equal([new TriggerFilter("port", "3")], Assert.Single(rig.Plugin.Triggers).Filters);
    }

    [Fact]
    public async Task Invalid_input_answers_the_pages_texts_below_the_fields_and_saves_nothing()
    {
        await using var rig = new Rig();
        var a = rig.AddDevice("10.0.0.48");
        await rig.StartAsync();

        async Task<(string Field, string Error)> ErrorOf(string args)
        {
            var answer = Parse(await rig.Plugin.InvokeAsync("tool.save.preview", args, CancellationToken.None));
            var error = FirstError(answer, out var field);
            return (field, error);
        }

        Assert.Equal(("read_id", TriggerPageTools.ReadFirst), await ErrorOf($$"""{"name":"X","topic":"{{Fixture.VirtualInput}}","devices":["{{a.Id}}"]}"""));
        Assert.Equal(("read_id", TriggerPageTools.ReadEnded), await ErrorOf($$"""{"name":"X","read_id":"nope","topic":"{{Fixture.VirtualInput}}","devices":["{{a.Id}}"]}"""));
        Assert.Equal(("topic", "Choose an event."), await ErrorOf($$"""{"name":"X","devices":["{{a.Id}}"]}"""));
        Assert.Equal(("name", "Enter a name."), await ErrorOf($$"""{"name":" ","devices":["{{a.Id}}"]}"""));
        Assert.Equal(("devices", "Use device ids from oadm_devices_search."), await ErrorOf("""{"name":"X","devices":["lobby"]}"""));
        Assert.Equal(("trigger_id", TriggerPageTools.EnterTriggerId), await ErrorOf("""{"trigger_id":"x"}"""));
        Assert.Empty(rig.Plugin.Triggers);
        Assert.Equal(TriggerPageTools.ChooseDevices, FirstError(Parse(await rig.Plugin.InvokeAsync("tool.events_read", """{"device_ids":[],"devices_total":0}""", CancellationToken.None)), out _));
    }

    [Fact]
    public async Task On_off_copy_and_delete_work_on_the_callers_triggers_only_and_a_run_checks_again()
    {
        var ctx = new Testing.FakeCoreContext();
        var a = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var b = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var keyA = ctx.FakeScopes.Add("Building A", [a.Id]);
        var keyB = ctx.FakeScopes.Add("Building B", [b.Id]);
        await using var plugin = new DeviceTriggersPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = Testing.FakeCallContext.WithGroup(ctx, [a.Id], "anna", keyA);
        var ben = Testing.FakeCallContext.WithGroup(ctx, [b.Id], "ben", keyB);
        var reply = TriggerJson.Deserialize<SaveReply>(await ctx.InvokeAsync(plugin, anna, DeviceTriggersMethods.Save, TriggerJson.Serialize(new SaveRequest(
            new TriggerDefinition { Name = "Door", Topic = "t", EventName = "Virtual input", Enabled = false, DeviceIds = [a.Id] }))));
        var door = reply.Trigger!;
        var id = $$"""{"trigger_id":"{{door.Id}}"}""";

        // Another group's trigger is unknown, like on the page.
        foreach (var method in new[] { "tool.copy.preview", "tool.delete.preview", "tool.delete" })
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, ben, method, id));
            Assert.Equal(DeviceTriggersPlugin.NoLongerExists, refused.Message);
        }

        var on = $$"""{"trigger_id":"{{door.Id}}","enabled":true}""";
        var plan = Parse(await ctx.InvokeAsync(plugin, anna, "tool.set_enabled.preview", on));
        Assert.True(plan["enabled"]!.GetValue<bool>());
        Assert.False(Assert.Single(plugin.Triggers).Enabled); // the preview changed nothing
        await ctx.InvokeAsync(plugin, anna, "tool.set_enabled", on);
        Assert.True(Assert.Single(plugin.Triggers).Enabled);
        Assert.Contains("already on", Parse(await ctx.InvokeAsync(plugin, anna, "tool.set_enabled.preview", on))["warnings"]![0]!.GetValue<string>(), StringComparison.Ordinal);

        var copyPlan = Parse(await ctx.InvokeAsync(plugin, anna, "tool.copy.preview", id));
        Assert.Equal("Door (copy)", copyPlan["copy_name"]!.GetValue<string>());
        Assert.Single(plugin.Triggers);
        var copy = Parse(await ctx.InvokeAsync(plugin, anna, "tool.copy", id));
        Assert.Equal("Door (copy)", copy["name"]!.GetValue<string>());
        Assert.False(copy["enabled"]!.GetValue<bool>());
        Assert.Equal(2, plugin.Triggers.Count);

        var deletePlan = Parse(await ctx.InvokeAsync(plugin, anna, "tool.delete.preview", id));
        Assert.Equal("Door", deletePlan["name"]!.GetValue<string>());
        Assert.Equal(2, plugin.Triggers.Count);
        await ctx.InvokeAsync(plugin, anna, "tool.delete", id);
        Assert.DoesNotContain(plugin.Triggers, t => t.Id == door.Id);
        var twice = await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.InvokeAsync(plugin, anna, "tool.delete", id));
        Assert.Equal(DeviceTriggersPlugin.NoLongerExists, twice.Message);

        // A group's save of a device outside the group is refused before anything is stored.
        await Assert.ThrowsAnyAsync<Exception>(() => ctx.InvokeAsync(plugin, anna, "tool.save", $$"""{"name":"Gate","devices":["{{b.Id}}"]}"""));
        Assert.Single(plugin.Triggers);
    }
}
