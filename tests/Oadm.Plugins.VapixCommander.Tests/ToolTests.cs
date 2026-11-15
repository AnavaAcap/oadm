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
        Assert.DoesNotContain("library:users.add", ids);              // Users is a task
        Assert.Contains("library:events.mqtt.client.configure", ids); // its password comes through the secret channel
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
        Assert.Equal("command: " + CommanderTools.NeedsSecretOrFile, await Field($$"""{"command":"library:siren.profiles.upload","device_ids":["{{device.Id}}"]}"""));
        Assert.Equal("values: " + CommanderTools.SecretFromUser,
            await Field($$"""{"command":"library:events.mqtt.client.configure","device_ids":["{{device.Id}}"],"values":[{"field":"host","value":"b"},{"field":"clientId","value":"c"},{"field":"password","value":"typed"}]}"""));
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
    public async Task A_value_that_makes_the_request_one_of_an_OADM_task_is_refused_as_it_is_sent()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = [new DeviceApi("mqtt-client", "1.0")] });
        var admin = ctx.CallAsAllDevices();

        // A saved command whose path is a field: harmless as saved, the agent's value would make it a change of NTP.
        var anyCgi = plugin.Library.Find("events.mqtt.client.activate")!.Clone();
        anyCgi.Id = "custom.anycgi";
        anyCgi.Request.Path = "/axis-cgi/{{cgi}}.cgi";
        anyCgi.Fields = [new CommandField { Name = "cgi", Label = "CGI", Type = FieldTypes.String, Default = JsonSerializer.SerializeToElement("mqtt/client") }];
        var saved = CommandJson.Read<SaveCommandReply>(await ctx.InvokeAsync(plugin, admin, CommanderMethods.Save, JsonSerializer.Serialize(new SaveCommandRequest { Command = anyCgi }, CommandJson.Api)));
        Assert.Null(saved.Error);

        var refused = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, CommanderTools.CommandRun.PreviewMethod,
            $$"""{"command":"saved:custom.anycgi","device_ids":["{{device.Id}}"],"values":[{"field":"cgi","value":"ntp"}]}"""))!)!;
        Assert.Equal("values", refused["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(CommanderTools.HasOwnTask, refused["errors"]![0]!["error"]!.GetValue<string>());

        var plan = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, CommanderTools.CommandRun.PreviewMethod,
            $$"""{"command":"saved:custom.anycgi","device_ids":["{{device.Id}}"]}"""))!)!;
        Assert.Null(plan["errors"]); // the saved default stays allowed
        Assert.Empty(ctx.TaskRuns);
    }

    [Fact]
    public async Task A_change_of_the_time_settings_through_param_cgi_is_left_to_OADMs_tasks()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });
        var admin = ctx.CallAsAllDevices();
        var ntp = plugin.Library.Find("image.brightness.set")!.Clone();
        ntp.Id = "custom.ntpserver";
        ntp.Request.Query = new Dictionary<string, string> { ["action"] = "update", ["Time.NTP.Server"] = "{{value}}" };
        ntp.Fields = [new CommandField { Name = "value", Label = "Server", Type = FieldTypes.String, Default = JsonSerializer.SerializeToElement("10.0.0.1") }];
        Assert.Null(CommandJson.Read<SaveCommandReply>(await ctx.InvokeAsync(plugin, admin, CommanderMethods.Save,
            JsonSerializer.Serialize(new SaveCommandRequest { Command = ntp }, CommandJson.Api))).Error);

        Assert.DoesNotContain((await AllCommandsAsync(ctx, plugin, admin)).Select(r => r["id"]!.GetValue<string>()), id => id == "saved:custom.ntpserver");
        var refused = JsonNode.Parse((await ctx.InvokeAsync(plugin, admin, CommanderTools.CommandRun.PreviewMethod,
            $$"""{"command":"saved:custom.ntpserver","device_ids":["{{device.Id}}"]}"""))!)!;
        Assert.Equal(CommanderTools.HasOwnTask, refused["errors"]![0]!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_run_of_an_agent_masks_secret_values_and_runs_the_definition_it_checked()
    {
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = ParamApi });

        await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), CommanderTools.CommandRun.RunMethod, $$"""{"command":"library:network.snmp.get","device_ids":["{{device.Id}}"]}""");

        using var payload = JsonDocument.Parse(Assert.Single(ctx.TaskRuns).PayloadJson!);
        Assert.True(payload.RootElement.GetProperty("maskSecrets").GetBoolean());
        Assert.Equal("network.snmp.get", payload.RootElement.GetProperty("commands")[0].GetProperty("command").GetProperty("id").GetString());

        // The labelled values of the real answer (10.0.0.48): only the community is hidden; a "...Key" setting with "no" hides nothing else.
        var snmp = plugin.Library.Find("network.snmp.get")!;
        var outcome = new CommandOutcome
        {
            Success = true,
            Summary = "Enabled: no · SNMP v1: no · Read community: public · Traps: no",
            Values = [new NamedValue("Enabled", "no"), new NamedValue("SNMP v1", "no"), new NamedValue("Read community", "public"), new NamedValue("Traps", "no")],
            Parameters = [new NamedValue("SNMP.V1ReadCommunity", "public"), new NamedValue("SNMP.V3.AuthKey", "no")],
        };
        Assert.Equal("Enabled: no · SNMP v1: no · Read community: *** · Traps: no", SecretValues.Mask(outcome.Summary, outcome, snmp));
        var text = new CommandOutcome { Success = true, Summary = "community s3cretvalue, enabled yes", Parameters = [new NamedValue("SNMP.V1ReadCommunity", "s3cretvalue"), new NamedValue("SNMP.V3.AuthKey", "yes")] };
        Assert.Equal("community ***, enabled yes", SecretValues.Mask(text.Summary, text));
        Assert.True(SecretValues.IsSecretName("MQTT.Password"));
        Assert.False(SecretValues.IsSecretName("ImageSource.I0.Sensor.Brightness"));
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

    [Fact]
    public async Task A_password_field_is_a_slot_the_user_fills_and_only_the_run_gets_it_masked()
    {
        const string Canary = "canary-mqtt-pass-4Rt";
        var (ctx, plugin) = await StartAsync();
        var device = ctx.Add(new SdkTesting.FakeDevice { Apis = [new DeviceApi("mqtt-client", "1.0")] });
        var call = ctx.CallAsAllDevices();
        var args = new JsonObject
        {
            ["command"] = "library:events.mqtt.client.configure",
            ["device_ids"] = new JsonArray(device.Id.ToString()),
            ["values"] = new JsonArray(new JsonObject { ["field"] = "host", ["value"] = "broker" }, new JsonObject { ["field"] = "clientId", ["value"] = "cam1" }),
        };

        var plan = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.PreviewMethod, args.ToJsonString()))!)!.AsObject();
        var slot = Assert.Single(plan["secrets_needed"]!.AsArray())!;
        Assert.Equal(("password", "password"), (slot["name"]!.GetValue<string>(), slot["label"]!.GetValue<string>()));
        Assert.Equal(1, plan["will_run"]!.GetValue<int>());

        // The host checks an entered value with the preview: a value the field does not take answers at secrets.password.
        var check = (JsonObject)args.DeepClone();
        check["secrets"] = new JsonObject { ["password"] = "with\0nul" };
        var refused = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.PreviewMethod, check.ToJsonString()))!)!;
        Assert.Equal("secrets.password", refused["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Empty(ctx.TaskRuns);

        // Without the value the run never sends a stand-in.
        var missing = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.RunMethod, args.ToJsonString()))!)!;
        Assert.NotNull(missing["errors"]);
        Assert.Empty(ctx.TaskRuns);

        var run = (JsonObject)args.DeepClone();
        run["secrets"] = new JsonObject { ["password"] = Canary };
        var answer = await ctx.InvokeAsync(plugin, call, CommanderTools.CommandRun.RunMethod, run.ToJsonString());
        Assert.DoesNotContain(Canary, answer, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(Assert.Single(ctx.TaskRuns).PayloadJson!);
        Assert.True(payload.RootElement.GetProperty("maskSecrets").GetBoolean());
        Assert.Contains(Canary, payload.RootElement.GetProperty("commands")[0].GetProperty("values").GetRawText(), StringComparison.Ordinal); // in memory, for the run
    }
}
