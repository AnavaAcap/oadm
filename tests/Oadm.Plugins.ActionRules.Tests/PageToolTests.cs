using System.Text.Json.Nodes;

using Oadm.Plugins.ActionEngine;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Plugins.ActionRules.Tools;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// The editor's page actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Read, the recipients and schedules of
/// the last read, and their New, Edit, Copy and Delete as a preview (the page's checks, nothing started) and a run (the page's
/// run, one task per device); the rights of the page methods; passwords never taken or shown; the caller's devices only.
/// </summary>
public sealed class PageToolTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";
    private const string Ftp = "com.axis.recipient.ftp";
    private const string VirtualInputTopic = "tns1:Device/tnsaxis:IO/VirtualInput";
    private const string NightId = "com.axis.schedules.oadm.night";
    private const string Night = "DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=DAILY";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-tools-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _a = new() { Address = "10.0.0.1" };
    private readonly FakeDevice _b = new() { Address = "10.0.0.2" };
    private readonly FakeDevices _devices = new();
    private readonly FakeCameras _cameras = new();
    private readonly RecordingRunner _runner = new();
    private readonly ActionRulesPlugin _plugin = new();

    public async Task InitializeAsync()
    {
        foreach (var device in new[] { _a, _b })
        {
            _devices.Items.Add(device);
            var camera = new RuleCamera("os12");
            camera.Recipients["90"] = new DeviceRecipient("90", "Upload", Ftp, [new("host", "ftp.example"), new("password", "s3cret-pw")]);
            camera.Schedules[NightId] = new DeviceSchedule(NightId, "Night", Night);
            camera.Add("Night door", Tcp, [], new EventFilter(VirtualInputTopic, null), [RuleText.ScheduleCondition(NightId)]);
            _cameras.Cameras[device.Id] = camera;
        }

        await _plugin.StartAsync(new FakeContext(_devices, _cameras, _runner, new RecordingEvents(), _data), CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static string Devices(params FakeDevice[] devices) =>
        $"\"device_ids\":{new JsonArray([.. devices.Select(d => (JsonNode)d.Id.ToString())]).ToJsonString()},\"devices_total\":{devices.Length}";

    private async Task<JsonNode> CallAsync(string method, string payload) =>
        JsonNode.Parse((await _plugin.InvokeAsync(method, payload, CancellationToken.None))!)!;

    private async Task ReadAsync()
    {
        var read = await CallAsync("tool.read", """{"all_devices":true}""");
        Assert.NotNull(read["reading"]);
        await _plugin.Sweep!.Current;
        Assert.Equal(2, (await CallAsync("tool.recipients", "{}"))["devices"]!.GetValue<int>());
    }

    private static string FirstError(JsonNode answer) => answer["errors"]![0]!["error"]!.GetValue<string>();

    [Fact]
    public void Every_tool_has_the_rights_of_the_page_method_it_wraps_and_a_preview_is_never_audited()
    {
        ICorePlugin core = _plugin;
        var set = ActionRulePageTools.Set;
        Assert.Equal(set.Tools.Count + 1, core.Tools.Count);
        foreach (var tool in set.Tools)
        {
            var page = set.PageMethodOf(tool.Method)!;
            Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(tool.Method));
            Assert.Equal(core.RequiredRole(page), core.RequiredRole(tool.Method));
            Assert.Equal(core.IsAudited(page), core.IsAudited(tool.Method));
            Assert.True(tool.Name.Length <= PluginTool.MaxNameLength);
            if (tool.Changes)
            {
                Assert.Equal(core.RequiredPermission(page), core.RequiredPermission(tool.PreviewMethod));
                Assert.False(core.IsAudited(tool.PreviewMethod));
                Assert.True(core.IsAudited(tool.Method));
                Assert.Equal(tool.Name.EndsWith("_delete", StringComparison.Ordinal), tool.Destructive);
            }
        }

        Assert.Equal(PermissionKeys.Show, core.RequiredPermission("tool.read"));
        Assert.Equal(ActionRulesMethods.ManagePermission, core.RequiredPermission("tool.recipient_create"));
        Assert.Equal(ActionRulesMethods.DeletePermission, core.RequiredPermission("tool.schedule_delete.preview"));
        Assert.DoesNotContain(set.Tools, t => t.Name.StartsWith("rule", StringComparison.Ordinal)); // rule changes are the editor's task
    }

    [Fact]
    public async Task Read_starts_the_page_read_and_the_reads_answer_its_rows_without_a_password()
    {
        Assert.Equal(0, (await CallAsync("tool.recipients", "{}"))["devices"]!.GetValue<int>());
        await ReadAsync();
        var requests = _cameras.Cameras.Values.Cast<RuleCamera>().Sum(c => c.Requests.Count);

        var recipients = await CallAsync("tool.recipients", """{"kind":"com.axis.recipient.ftp"}""");
        var upload = recipients["recipients"]!.AsArray().Single(r => r!["name"]!.GetValue<string>() == "Upload")!;
        Assert.Equal((2, 2), (upload["devices"]!.GetValue<int>(), upload["with_password"]!.GetValue<int>()));
        var detail = await CallAsync("tool.recipients", $$"""{"device_id":"{{_a.Id}}","search":"Upload"}""");
        var mine = Assert.Single(detail["details"]!.AsArray())!;
        Assert.Equal(["password"], mine["passwords_set"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.DoesNotContain("s3cret-pw", detail.ToJsonString(), StringComparison.Ordinal);

        var schedules = await CallAsync("tool.schedules", """{"search":"Night"}""");
        var night = schedules["schedules"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == "Night")!;
        Assert.Equal((2, 2, false), (night["devices"]!.GetValue<int>(), night["used_by"]!.GetValue<int>(), night["built_in"]!.GetValue<bool>()));

        var kinds = await CallAsync("tool.recipient_kinds", "{" + Devices(_a, _b) + "}");
        Assert.Contains(kinds["kinds"]!.AsArray(), k => k!["kind"]!.GetValue<string>() == Ftp && k["devices"]!.GetValue<int>() == 2);
        Assert.Equal(requests, _cameras.Cameras.Values.Cast<RuleCamera>().Sum(c => c.Requests.Count)); // the reads asked no device
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task A_recipient_change_previews_without_starting_and_runs_the_page_run_once()
    {
        await ReadAsync();
        var payload = "{" + Devices(_a, _b) + ""","name":"Upload","kind":"com.axis.recipient.ftp","settings":[{"name":"host","value":"ftp2.example"}]}""";

        var plan = await CallAsync("tool.recipient_change.preview", payload);
        Assert.Equal((2, "device"), (plan["will_change"]!.GetValue<int>(), plan["unit"]!.GetValue<string>()));
        Assert.Equal(["host"], plan["settings"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Empty(_runner.Runs); // the preview starts nothing
        Assert.Empty(await _plugin.Undo.ListAsync(CancellationToken.None)); // and saves no run

        var run = await CallAsync("tool.recipient_change", payload);
        Assert.Equal(2, run["tasks"]!.GetValue<int>());
        var started = Assert.Single(_runner.Runs);
        var task = ActionRulesJson.Deserialize<RuleTaskPayload>(started.Payload);
        Assert.Equal((RuleTaskActions.RecipientEdit, "ftp2.example", 2), (task.Action, task.RecipientPatch!.Parameters!["host"], task.Targets.Count));
        Assert.Equal(run["run_id"]!.GetValue<string>(), task.RunId.ToString());
    }

    [Fact]
    public async Task Passwords_are_never_taken_and_invalid_input_gets_the_pages_texts()
    {
        await ReadAsync();
        var secret = await CallAsync("tool.recipient_change.preview", "{" + Devices(_a) + ""","name":"Upload","kind":"com.axis.recipient.ftp","settings":[{"name":"password","value":"x"}]}""");
        Assert.Equal(ActionRulePageTools.SetInOadm, FirstError(secret));
        var login = await CallAsync("tool.recipient_create.preview", "{" + Devices(_a) + ""","name":"Up","kind":"com.axis.recipient.http","settings":[{"name":"upload_url","value":"http://user:pw@host/x"}]}""");
        Assert.Equal(ActionRulePageTools.SetInOadm, FirstError(login));
        var none = await CallAsync("tool.recipient_delete.preview", "{" + Devices(_a) + ""","name":"Nobody","kind":"com.axis.recipient.ftp"}""");
        Assert.Equal(ActionRulePageTools.NoneMatch, FirstError(none));
        var noDevices = await CallAsync("tool.read", """{"device_ids":[],"devices_total":0}""");
        Assert.Equal(ActionRulePageTools.ChooseDevices, FirstError(noDevices));
        var badName = await CallAsync("tool.schedule_create.preview", "{" + Devices(_a) + $$""","name":"{{new string('n', 70)}}","definition":"x"}""");
        Assert.Equal("name", badName["errors"]![0]!["field"]!.GetValue<string>()); // the length bound
        var badTimes = await CallAsync("tool.schedule_create.preview", "{" + Devices(_a) + ""","name":"Late","definition":"RRULE:FREQ=NEVER"}""");
        Assert.Equal("definition", badTimes["errors"]![0]!["field"]!.GetValue<string>());
        var change = await CallAsync("tool.schedule_change.preview", "{" + Devices(_a) + ""","name":"Night"}""");
        Assert.Equal("Choose what to change.", FirstError(change));
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task A_schedule_rules_use_is_deleted_only_with_its_rules()
    {
        await ReadAsync();
        var payload = "{" + Devices(_a, _b) + ""","name":"Night"}""";
        Assert.Equal(ActionRulePageTools.RulesUseIt, FirstError(await CallAsync("tool.schedule_delete.preview", payload)));

        var with = "{" + Devices(_a, _b) + ""","name":"Night","delete_rules":true}""";
        var plan = await CallAsync("tool.schedule_delete.preview", with);
        Assert.Equal((2, 2), (plan["will_change"]!.GetValue<int>(), plan["rules_deleted"]!.GetValue<int>()));
        Assert.Empty(_runner.Runs);
        await CallAsync("tool.schedule_delete", with);
        var task = ActionRulesJson.Deserialize<RuleTaskPayload>(Assert.Single(_runner.Runs).Payload);
        Assert.Equal((RuleTaskActions.ScheduleDelete, 2), (task.Action, task.Rules!.Count));

        // A built-in schedule is never a target.
        var builtIn = await CallAsync("tool.schedule_change.preview", "{" + Devices(_a) + ""","name":"Office Hours","new_name":"x"}""");
        Assert.Equal(ActionRulePageTools.BuiltIn, FirstError(builtIn));
    }

    [Fact]
    public async Task A_new_schedule_and_copies_start_the_pages_runs()
    {
        await ReadAsync();
        var created = await CallAsync("tool.schedule_create", "{" + Devices(_a, _b) + ""","name":"Late shift","definition":"DTSTART:19700101T180000\nDTEND:19700101T230000\nRRULE:FREQ=DAILY"}""");
        Assert.Equal(2, created["tasks"]!.GetValue<int>());
        var task = ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload);
        Assert.Equal((RuleTaskActions.ScheduleDeploy, "com.axis.schedules.oadm.late-shift"), (task.Action, task.Schedules![0].Id));

        var copy = await CallAsync("tool.recipient_copy.preview", "{" + Devices(_b) + $$""","source_device_id":"{{_a.Id}}","name":"Upload","kind":"com.axis.recipient.ftp"}""");
        Assert.Equal(1, copy["will_change"]!.GetValue<int>());
        Assert.DoesNotContain("s3cret-pw", copy.ToJsonString(), StringComparison.Ordinal);
        await CallAsync("tool.recipient_copy", "{" + Devices(_b) + $$""","source_device_id":"{{_a.Id}}","name":"Upload","kind":"com.axis.recipient.ftp"}""");
        task = ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload);
        Assert.Equal((RuleTaskActions.RecipientDeploy, "Upload"), (task.Action, task.Recipient!.Name)); // the page's copy, its password in memory only
        Assert.Equal([_b.Id], _runner.Runs[^1].DeviceIds);

        var scheduleCopy = await CallAsync("tool.schedule_copy", "{" + Devices(_b) + $$""","source_device_id":"{{_a.Id}}","name":"Night"}""");
        Assert.Equal(1, scheduleCopy["tasks"]!.GetValue<int>());

        var recipient = await CallAsync("tool.recipient_create", "{" + Devices(_a) + ""","name":"Alarm server","kind":"com.axis.recipient.tcp","settings":[{"name":"host","value":"192.0.2.90"},{"name":"port","value":"7000"}]}""");
        Assert.Equal(1, recipient["tasks"]!.GetValue<int>());
        task = ActionRulesJson.Deserialize<RuleTaskPayload>(_runner.Runs[^1].Payload);
        Assert.Equal("192.0.2.90", task.Recipient!.Parameters.Single(p => p.Name == "host").Value);
    }

    [Fact]
    public async Task No_tool_reaches_a_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        await using var plugin = new ActionRulesPlugin(new DeviceSweepOptions { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) });
        await plugin.StartAsync(ctx, CancellationToken.None);
        const string Chosen = "\"device_ids\":[\"{inside}\",\"{device}\"],\"devices_total\":2";

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method("tool.read", "{" + Chosen + "}")
            .Method("tool.recipient_kinds", "{" + Chosen + "}")
            .Method("tool.recipient_create.preview", "{" + Chosen + ""","name":"A","kind":"com.axis.recipient.tcp"}""")
            .Method("tool.recipient_create", "{" + Chosen + ""","name":"A","kind":"com.axis.recipient.tcp"}""")
            .Method("tool.recipient_change", "{" + Chosen + ""","name":"A","kind":"com.axis.recipient.tcp","new_name":"B"}""")
            .Method("tool.recipient_delete", "{" + Chosen + ""","name":"A","kind":"com.axis.recipient.tcp"}""")
            .Method("tool.recipient_copy", """{"device_ids":["{inside}"],"devices_total":1,"source_device_id":"{device}","name":"A","kind":"com.axis.recipient.tcp"}""")
            .Method("tool.recipient_copy", """{"device_ids":["{device}"],"devices_total":1,"source_device_id":"{inside}","name":"A","kind":"com.axis.recipient.tcp"}""")
            .Method("tool.schedule_create", "{" + Chosen + $$""","name":"Night","definition":"{{Night.Replace("\n", "\\n", StringComparison.Ordinal)}}"}""")
            .Method("tool.schedule_change", "{" + Chosen + ""","name":"Night","new_name":"Day"}""")
            .Method("tool.schedule_delete", "{" + Chosen + ""","name":"Night","delete_rules":true}""")
            .Method("tool.schedule_copy", """{"device_ids":["{inside}"],"devices_total":1,"source_device_id":"{device}","name":"Night"}""")
            .WithoutDevices("tool.recipients", "the table of the caller's own group")
            .WithoutDevices("tool.schedules", "the table of the caller's own group")
            .RunAsync();
    }
}
