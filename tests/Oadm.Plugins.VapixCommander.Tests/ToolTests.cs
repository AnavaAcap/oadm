using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// The tools for AI agents (docs/spec/mcp.md "Plugins"): only library and saved commands without a password or a file field, never
/// a dangerous one or one OADM's own tasks do; every run goes preview then run, on the caller's devices only.
/// </summary>
public sealed class ToolTests
{
    private static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "Library");

    private static readonly IReadOnlyList<DeviceApi> ParamApi = [new DeviceApi("param-cgi", "1.0")];

    private static async Task<(SdkTesting.FakeCoreContext Ctx, VapixCommanderPlugin Plugin)> StartAsync()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var plugin = new VapixCommanderPlugin(LibraryDirectory);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (ctx, plugin);
    }

    private static async Task<List<JsonObject>> AllCommandsAsync(SdkTesting.FakeCoreContext ctx, VapixCommanderPlugin plugin, ICallContext call)
    {
        var rows = new List<JsonObject>();
        string? after = null;
        do
        {
            var payload = new JsonObject { ["limit"] = 200 };
            if (after is not null)
            {
                payload["after"] = after;
            }

            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.Commands.Method, payload.ToJsonString()))!)!.AsObject();
            rows.AddRange(answer["commands"]!.AsArray().Select(r => r!.AsObject()));
            after = answer["next"]?.GetValue<string>();
        }
        while (after is not null);

        return rows;
    }

    [Fact]
    public async Task The_list_offers_no_dangerous_command_none_with_a_password_or_file_and_none_OADM_does_itself()
    {
        var (ctx, plugin) = await StartAsync();

        var ids = (await AllCommandsAsync(ctx, plugin, ctx.CallAsAllDevices())).Select(r => r["id"]!.GetValue<string>()).ToHashSet();

        Assert.Contains("library:image.settings.get", ids);           // a read
        Assert.Contains("library:image.brightness.set", ids);         // a change
        Assert.Contains("library:image.optics.autofocus", ids);       // autofocus is no task of OADM
        Assert.Contains("library:time.get", ids);                     // a read of a path whose changes OADM makes
        Assert.DoesNotContain("library:maintenance.restart", ids);    // dangerous, and Restart is a task
        Assert.DoesNotContain("library:storage.format", ids);         // dangerous
        Assert.DoesNotContain("library:users.add", ids);              // a password
        Assert.DoesNotContain("library:siren.profiles.upload", ids);  // a file
        Assert.DoesNotContain("library:time.timezone.set", ids);      // Date and time is a task
        Assert.DoesNotContain("library:time.ntp.static.set", ids);    // Use OADM as NTP server is a task
        Assert.DoesNotContain("library:apps.start", ids);             // Applications are tasks
        Assert.DoesNotContain("library:maintenance.firmware.commit", ids); // AXIS OS is a task
        foreach (var command in plugin.Library.Commands)
        {
            Assert.Equal(CommanderTools.Refusal(command) is null, ids.Contains("library:" + command.Id));
        }
    }

    [Fact]
    public async Task A_page_stays_within_the_answer_budget_and_the_cursor_reaches_every_command()
    {
        var (ctx, plugin) = await StartAsync();
        var call = ctx.CallAsAllDevices();

        var first = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.Commands.Method, """{"limit":200}"""))!)!.AsObject();
        Assert.True(first.ToJsonString().Length < 32 * 1024, "one page fits the 32 KB answer");
        var all = await AllCommandsAsync(ctx, plugin, call);
        Assert.Equal(first["total"]!.GetValue<int>(), all.Count);
        Assert.Equal(all.Count, all.Select(r => r["id"]!.GetValue<string>()).Distinct().Count());
    }

    [Fact]
    public async Task The_preview_checks_values_and_devices_and_starts_nothing()
    {
        var (ctx, plugin) = await StartAsync();
        var ready = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi, HostName = "lobby" });
        var old = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11", Apis = [new DeviceApi("io-port-management", "1.0")] }); // no param API
        var call = ctx.CallAsAllDevices();

        var plan = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.PreviewMethod,
            $$"""{"command":"library:image.brightness.set","device_ids":["{{ready.Id}}","{{old.Id}}"],"values":[{"field":"value","value":"60"}]}"""))!)!.AsObject();

        Assert.Equal("change", plan["kind"]!.GetValue<string>());
        Assert.Equal(1, plan["will_run"]!.GetValue<int>());
        Assert.Equal("lobby", plan["devices"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(1, plan["skipped"]![0]!["count"]!.GetValue<int>());
        Assert.Contains("60", plan["request"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(ctx.TaskRuns);
    }

    [Fact]
    public async Task Wrong_input_answers_per_field_and_starts_nothing()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var call = ctx.CallAsAllDevices();

        async Task<string> Field(string payload)
        {
            var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.PreviewMethod, payload))!)!;
            return answer["errors"]![0]!["field"]!.GetValue<string>() + ": " + answer["errors"]![0]!["error"]!.GetValue<string>();
        }

        Assert.StartsWith("values: ", await Field($$"""{"command":"library:image.brightness.set","device_ids":["{{device.Id}}"],"values":[{"field":"value","value":"many"}]}"""), StringComparison.Ordinal);
        Assert.Equal("values: The command has no field \"color\". Take the fields from oadm_vapix_commands.",
            await Field($$"""{"command":"library:image.brightness.set","device_ids":["{{device.Id}}"],"values":[{"field":"color","value":"1"}]}"""));
        Assert.Equal("command: " + CommanderTools.IsDangerous, await Field($$"""{"command":"library:maintenance.restart","device_ids":["{{device.Id}}"]}"""));
        Assert.Equal("command: " + CommanderTools.HasOwnTask, await Field($$"""{"command":"library:time.timezone.set","device_ids":["{{device.Id}}"]}"""));
        Assert.Equal("command: " + CommanderTools.NeedsSecretOrFile, await Field($$"""{"command":"library:users.password.set","device_ids":["{{device.Id}}"]}"""));
        Assert.Equal("command: " + CommanderTools.NotForAgents, await Field($$"""{"command":"inline:x","device_ids":["{{device.Id}}"]}"""));
        Assert.Equal("device_ids: Take the device ids from oadm_devices_search.", await Field("""{"command":"library:image.settings.get","device_ids":["lobby"]}"""));
        Assert.Equal("device_ids: Choose at least one device.", await Field("""{"command":"library:image.settings.get","device_ids":[]}"""));
        Assert.Empty(ctx.TaskRuns);
    }

    [Fact]
    public async Task The_run_starts_one_task_per_ready_device_with_the_users_confirmation()
    {
        var (ctx, plugin) = await StartAsync();
        var ready = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var old = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11", Apis = [new DeviceApi("io-port-management", "1.0")] });
        var call = ctx.CallAsAllDevices("anna");

        var answer = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.RunMethod,
            $$"""{"command":"library:image.brightness.set","device_ids":["{{ready.Id}}","{{old.Id}}"],"values":[{"field":"value","value":"60"}]}"""))!)!;

        Assert.Equal(1, answer["tasks"]!.GetValue<int>());
        Assert.Equal(1, answer["skipped"]!.GetValue<int>());
        var run = Assert.Single(ctx.TaskRuns);
        Assert.Equal([ready.Id], run.DeviceIds);
        Assert.Equal("anna", run.Owner);
        using var payload = JsonDocument.Parse(run.PayloadJson!);
        Assert.True(payload.RootElement.GetProperty("confirmed").GetBoolean());
        Assert.False(payload.RootElement.GetProperty("confirmedDangerous").GetBoolean());
        Assert.True(plugin.IsAudited(CommanderTools.CommandRun.RunMethod));
        Assert.False(plugin.IsAudited(CommanderTools.CommandRun.PreviewMethod));
    }

    [Fact]
    public async Task A_read_also_goes_preview_then_run()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var call = ctx.CallAsAllDevices();
        var payload = $$"""{"command":"library:image.settings.get","device_ids":["{{device.Id}}"]}""";

        var plan = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.PreviewMethod, payload))!)!;
        Assert.Equal("read", plan["kind"]!.GetValue<string>());
        Assert.Empty(ctx.TaskRuns);

        await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.RunMethod, payload);
        Assert.Single(ctx.TaskRuns);
    }

    [Fact]
    public async Task A_device_outside_the_users_group_refuses_the_whole_call()
    {
        var (ctx, plugin) = await StartAsync();
        var inside = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var outside = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi, Address = "192.0.2.11" });
        var call = ctx.CallAsGroup([inside.Id], "tech2");

        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.RunMethod,
            $$"""{"command":"library:image.settings.get","device_ids":["{{inside.Id}}","{{outside.Id}}"]}"""));
        Assert.Empty(ctx.TaskRuns);
    }

    [Fact]
    public async Task A_saved_command_follows_the_same_rules()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var admin = ctx.CallAsAllDevices();
        var brightness = plugin.Library.Find("image.brightness.set")!.Clone();
        brightness.Id = "custom.brightness";
        var restart = plugin.Library.Find("maintenance.restart")!.Clone();
        restart.Id = "custom.restart";
        foreach (var command in new[] { brightness, restart })
        {
            await ctx.InvokeAsync(plugin, admin, CommanderMethods.Save, JsonSerializer.Serialize(new SaveCommandRequest { Command = command }, CommandJson.Api));
        }

        var ids = (await AllCommandsAsync(ctx, plugin, admin)).Select(r => r["id"]!.GetValue<string>()).ToList();
        Assert.Contains("saved:custom.brightness", ids);
        Assert.DoesNotContain("saved:custom.restart", ids);

        await ctx.InvokeAsync(plugin, admin, CommanderTools.CommandRun.RunMethod, $$"""{"command":"saved:custom.brightness","device_ids":["{{device.Id}}"]}""");
        Assert.Single(ctx.TaskRuns);
    }

    [Fact]
    public void The_tools_need_the_rollout_right_and_are_named_vapix()
    {
        var plugin = new VapixCommanderPlugin(LibraryDirectory);

        Assert.Equal("vapix", plugin.ToolArea);
        Assert.Equal("rollout", plugin.RequiredPermission(CommanderTools.CommandRun.PreviewMethod));
        Assert.Equal("rollout", plugin.RequiredPermission(CommanderTools.CommandRun.RunMethod));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(CommanderTools.Commands.Method));
        Assert.True(CommanderTools.CommandRun.Changes);
        Assert.Equal("rollout", CommanderTools.CommandRun.Permission);
    }
}
