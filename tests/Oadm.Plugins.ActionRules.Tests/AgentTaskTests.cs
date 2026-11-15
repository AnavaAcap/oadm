using System.Text.Json.Nodes;

using Oadm.Plugins.ActionRules.Agent;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tasks;

using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// The task's input for AI agents (docs/spec/mcp.md "Tasks"): its schema, the page's checks at the fields, the plan from the
/// editor's last read, the page's rights, and runs that do what the page's methods do (saved for undo, a copy read once, undo).
/// </summary>
public sealed class AgentTaskTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";
    private const string Ftp = "com.axis.action.fixed.send_images.ftp";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-agent-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevices _devices = new();
    private readonly FakeCameras _cameras = new();
    private readonly ActionRulesPlugin _plugin = new();
    private FakeContext? _ctx;

    private readonly FakeDevice _a = new() { Address = "10.0.0.1" };
    private readonly FakeDevice _b = new() { Address = "10.0.0.2" };
    private readonly FakeDevice _unread = new() { Address = "10.0.0.3" };

    public async Task InitializeAsync()
    {
        _devices.Items.Add(_a);
        _devices.Items.Add(_b);
        _ctx = new FakeContext(_devices, _cameras, new RecordingRunner(), new RecordingEvents(), _data);
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private RuleTaskPlugin Task => (RuleTaskPlugin)_plugin.TaskPlugins[0];

    private ICallContext Caller => new ContextCall(_ctx!);

    private static readonly EventFilter VirtualInput = new(
        "tns1:Device/tnsaxis:IO/VirtualInput",
        "boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");

    private static string Json(RuleAgentPayload payload) => PluginJson.Serialize(payload, RuleAgentPayload.JsonOptions);

    private RuleCamera Camera(FakeDevice device, string os = "os12") => _cameras.Cameras.GetOrAdd(device.Id, _ => new RuleCamera(os));

    /// <summary>"Door" (on) on both cameras, "Spare" (off) on <see cref="_a"/> only; the editor reads both.</summary>
    private async Task ReadAsync()
    {
        foreach (var device in new[] { _a, _b })
        {
            Camera(device).Add("Door", Tcp, [("port", "9999"), ("qos", "0"), ("message", "door"), ("host", "192.0.2.10")], start: VirtualInput);
        }

        Camera(_a).Add("Spare", Tcp, [("port", "9999"), ("qos", "0"), ("message", "spare"), ("host", "192.0.2.10")], start: VirtualInput, enabled: false);
        await _plugin.InvokeAsync(ActionRulesMethods.Read, null, CancellationToken.None);
        await _plugin.Sweep!.Current;
    }

    private async Task<RecordingContext> RunAsync(FakeDevice device, string json)
    {
        var ctx = new RecordingContext(Camera(device)) { Caller = Caller };
        await StepRun.RunAsync(ctx.Steps, () => Task.ExecuteAsync(ctx, device, json, CancellationToken.None));
        return ctx;
    }

    private Dictionary<string, string> Errors(RuleAgentPayload payload) =>
        Task.ValidatePayload(Json(payload), Caller).ToDictionary(e => e.Field, e => e.Message);

    [Fact]
    public void The_schema_offers_the_editor_operations_without_a_password_field()
    {
        var schema = TaskPayloadSchemas.Of(Task);
        Assert.NotNull(schema);
        Assert.Equal(["change", "delete", "copy", "create", "undo"], schema!["properties"]!["action"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(["action"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.DoesNotContain("runId", schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.DoesNotContain("$ref", schema.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"password", schema.ToJsonString(), StringComparison.OrdinalIgnoreCase); // no password field anywhere
    }

    [Fact]
    public void The_page_payloads_pass_and_start_only_from_the_page()
    {
        var edit = ActionRulesJson.Serialize(new RuleTaskPayload(RuleTaskActions.Edit, Guid.NewGuid(), "Turn off action rules", [new DeviceTargets(_a.Id, [new RuleRef("1", "v")])], new RulePatch { Enabled = false }));
        var deploy = ActionRulesJson.Serialize(new RuleTaskPayload(RuleTaskActions.Deploy, Guid.NewGuid(), "Copy", [], null)
        {
            Rule = new DeviceRule(string.Empty, "Door", true, VirtualInput, [], null, new RuleAction(Tcp, [])),
            Schedules = [],
        });
        Assert.Empty(Task.ValidatePayload(edit, Caller));
        Assert.Empty(Task.ValidatePayload(deploy, Caller));
        Assert.Null(Task.PagePermissions(edit));
        Assert.Null(Task.PagePermissions(deploy));
        Assert.Equal("Turn off action rules", Task.GetTaskName(edit));

        var bad = ActionRulesJson.Serialize(new RuleTaskPayload(RuleTaskActions.Edit, Guid.NewGuid(), "Change", [], null));
        Assert.Equal("Choose what to change. Nothing was changed.", Assert.Single(Task.ValidatePayload(bad, Caller)).Message);
    }

    [Fact]
    public void Each_operation_needs_the_right_of_the_page_method_it_stands_for()
    {
        Assert.Equal([ActionRulesMethods.DeletePermission], Task.PagePermissions(Json(new(RuleAgentAction.Delete, RuleName: "Door"))));
        foreach (var action in new[] { RuleAgentAction.Change, RuleAgentAction.Copy, RuleAgentAction.Create, RuleAgentAction.Undo })
        {
            Assert.Equal([ActionRulesMethods.ManagePermission], Task.PagePermissions(Json(new(action))));
        }

        Assert.Equal([ActionRulesMethods.ManagePermission, ActionRulesMethods.DeletePermission], Task.PagePermissions("{\"action\":\"burn\"}"));
        Assert.Equal([ActionRulesMethods.ManagePermission, ActionRulesMethods.DeletePermission], Task.PagePermissions(null));
        Assert.Equal("Turn off action rules", Task.GetTaskName(Json(new(RuleAgentAction.Change, "Door", new RuleAgentChange(Enabled: false)))));
        Assert.Equal("Delete action rules", Task.GetTaskName(Json(new(RuleAgentAction.Delete, "Door"))));
    }

    [Fact]
    public async Task Invalid_input_answers_at_its_field_with_the_page_texts()
    {
        await ReadAsync();
        Assert.Equal(RuleAgentCheck.RuleNameText, Errors(new(RuleAgentAction.Change, Change: new(Enabled: false)))["ruleName"]);
        Assert.Equal("Choose what to change.", Errors(new(RuleAgentAction.Change, "Door"))["change"]);
        Assert.Equal("Choose at least one thing to change.", Errors(new(RuleAgentAction.Change, "Door", new()))["change"]);
        Assert.Equal("Enter a name of 1 to 128 characters.", Errors(new(RuleAgentAction.Change, "Door", new(Name: " ")))["change.name"]);
        Assert.Equal("Enter 0 to 86,400 seconds.", Errors(new(RuleAgentAction.Change, "Door", new(TimeoutSeconds: 90_000)))["change.timeoutSeconds"]);
        Assert.Equal("No device has the setting \"colour\".", Errors(new(RuleAgentAction.Change, "Door", new(Parameters: new Dictionary<string, string> { ["colour"] = "red" })))["change.parameters"]);
        Assert.Equal(RuleAgentCheck.SecretText, Errors(new(RuleAgentAction.Change, "Door", new(Parameters: new Dictionary<string, string> { ["password"] = "pw" })))["change.parameters.password"]);
        Assert.Equal(RuleAgentCheck.UnusedText, Errors(new(RuleAgentAction.Delete, "Door", new(Enabled: false)))["change"]);

        var copy = Errors(new(RuleAgentAction.Copy));
        Assert.Equal(RuleAgentCheck.SourceDeviceText, copy["sourceDeviceId"]);
        Assert.Equal("Choose the rule to copy.", copy["sourceRuleId"]);
        Assert.Equal(RuleAgentCheck.SourceNotReadText, Errors(new(RuleAgentAction.Copy, SourceDeviceId: _unread.Id, SourceRuleId: "1"))["sourceRuleId"]);

        Assert.Equal("Describe the rule.", Errors(new(RuleAgentAction.Create))["rule"]);
        Assert.Equal("Choose the event.", Errors(new(RuleAgentAction.Create, Rule: new("New", new RuleAgentEvent("bad topic"), Tcp)))["rule.event"]);
        Assert.Equal("No device offers this action.", Errors(new(RuleAgentAction.Create, Rule: new("New", new RuleAgentEvent(VirtualInput.Topic), "com.axis.action.none")))["rule.template"]);
        Assert.Equal(RuleAgentCheck.SecretText, Errors(new(RuleAgentAction.Create, Rule: new("New", new RuleAgentEvent(VirtualInput.Topic), Ftp, new Dictionary<string, string> { ["password"] = "pw" })))["rule.parameters.password"]);

        Assert.Equal("Choose the change to undo.", Errors(new(RuleAgentAction.Undo))["undoOf"]);
        Assert.Equal(RuleTexts.NothingToUndo, Errors(new(RuleAgentAction.Undo, UndoOf: Guid.NewGuid()))["undoOf"]);

        Assert.Empty(Errors(new(RuleAgentAction.Change, "Door", new(Enabled: false))));
        Assert.Empty(Errors(new(RuleAgentAction.Delete, "Door")));
        Assert.Empty(Errors(new(RuleAgentAction.Create, Rule: new("New", new RuleAgentEvent(VirtualInput.Topic, new Dictionary<string, string> { ["port"] = "20" }), Tcp, new Dictionary<string, string> { ["host"] = "192.0.2.10", ["port"] = "9999", ["qos"] = "0", ["message"] = "x" }))));
    }

    [Fact]
    public async Task The_plan_comes_from_the_last_read_and_says_what_a_delete_does()
    {
        await ReadAsync();
        var down = new FakeDevice { Address = "10.0.0.4", Status = DeviceStatus.Unreachable };
        IReadOnlyList<IDeviceInfo> devices = [_a, _b, _unread, down];

        var off = await Task.PlanAsync(Json(new(RuleAgentAction.Change, "Spare", new(Enabled: false))), devices, CancellationToken.None);
        Assert.Equal(["Already off.", ActionRulesPlugin.NoSuchRuleText, ActionRulesPlugin.NotReadText], off.Devices.Take(3).Select(d => d.SkipReason));
        Assert.NotNull(off.Devices[3].SkipReason); // unreachable, by its own reason

        var delete = await Task.PlanAsync(Json(new(RuleAgentAction.Delete, "Door")), devices, CancellationToken.None);
        Assert.Equal([null, null, ActionRulesPlugin.NotReadText], delete.Devices.Take(3).Select(d => d.SkipReason));
        Assert.Equal("Deletes 2 action rules on 2 devices. Undo in the Action Rule Editor puts them back.", Assert.Single(delete.Warnings));

        var copy = await Task.PlanAsync(Json(new(RuleAgentAction.Copy, SourceDeviceId: _a.Id, SourceRuleId: "1")), devices, CancellationToken.None);
        Assert.Equal(ActionRulesPlugin.SourceText, copy.Devices[0].SkipReason);
        Assert.Null(copy.Devices[1].SkipReason);
        Assert.Contains("stored passwords", Assert.Single(copy.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_input_writes_nothing_and_needs_the_editor_read()
    {
        await ReadAsync();
        var before = Camera(_a).Requests.Count;

        var empty = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(_a, Json(new(RuleAgentAction.Change, "Door", new()))));
        Assert.Equal("Choose at least one thing to change. Nothing was changed.", empty.Message);
        var secret = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(_a, Json(new(RuleAgentAction.Change, "Door", new(Parameters: new Dictionary<string, string> { ["password"] = "pw" })))));
        Assert.Equal(RuleAgentCheck.SecretText + " Nothing was changed.", secret.Message);
        Assert.Equal(before, Camera(_a).Requests.Count); // not even read

        _devices.Items.Add(_unread);
        var unread = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(_unread, Json(new(RuleAgentAction.Delete, "Door"))));
        Assert.Equal(ActionRulesPlugin.NotReadText + " Nothing was changed.", unread.Message);
        Assert.Empty(Camera(_unread).Requests);
        Assert.Empty(await _plugin.Undo.ListAsync(CancellationToken.None).ContinueWith(t => t.Result.Where(r => r.Action == RuleTaskActions.Edit).ToList(), TaskScheduler.Default));
    }

    [Fact]
    public async Task Turn_off_by_name_is_saved_for_undo_and_the_agent_undo_puts_it_back()
    {
        await ReadAsync();
        var json = Json(new(RuleAgentAction.Change, "Door", new(Enabled: false))); // one string per run, as the task engine hands it
        await RunAsync(_a, json);
        await RunAsync(_b, json);
        Assert.All(new[] { _a, _b }, d => Assert.False(Camera(d).Rules.Values.Single(r => r.Name == "Door").Enabled));
        Assert.True(Camera(_a).Rules.Values.Single(r => r.Name == "Spare").Enabled is false); // another name stays

        var run = Assert.Single(await _plugin.Undo.ListAsync(CancellationToken.None));
        Assert.Equal("Turn off action rules", run.Description);
        Assert.Equal(ICallContext.AllDevicesScopeKey, run.Scope);
        Assert.Equal(0, _plugin.OpenTasksOf(run.RunId)); // both tasks ended

        // The list tool names the change for undo.
        var list = JsonNode.Parse((await _plugin.InvokeAsync(Tools.ActionRuleTools.Rules.Method, null, CancellationToken.None))!)!;
        Assert.Equal(run.RunId.ToString(), list["changes"]![0]!["id"]!.GetValue<string>());

        var undo = Json(new(RuleAgentAction.Undo, UndoOf: run.RunId));
        Assert.Empty(Task.ValidatePayload(undo, Caller));
        var plan = await Task.PlanAsync(undo, [_a, _b, _unread], CancellationToken.None);
        Assert.Equal([null, null, ActionRulesPlugin.NotInChangeText], plan.Devices.Select(d => d.SkipReason));
        await RunAsync(_a, undo);
        await RunAsync(_b, undo);
        Assert.All(new[] { _a, _b }, d => Assert.True(Camera(d).Rules.Values.Single(r => r.Name == "Door").Enabled));
        Assert.True((await _plugin.Undo.FindAsync(run.RunId, CancellationToken.None))!.Undone);

        // Undone once: a second undo run of the same change refuses before it reads a device.
        var again = Json(new(RuleAgentAction.Undo, UndoOf: run.RunId));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => RunAsync(_a, again));
        Assert.Equal("This change was undone already. Nothing was changed.", refused.Message);
    }

    [Fact]
    public async Task A_copy_reads_its_source_once_with_the_passwords_on_the_server()
    {
        var source = Camera(_a);
        var id = source.Add("Upload", Ftp,
            [("stream_options", "x"), ("pre_duration", "0"), ("post_duration", "0"), ("max_images", "1"), ("create_folder", string.Empty), ("filename", "a.jpg"), ("max_sequence_number", "0"),
             ("host", "192.0.2.11"), ("upload_path", "x"), ("port", "21"), ("login", "u"), ("password", "secret1"), ("passive", "1"), ("temporary", "0"), ("qos", "0")],
            start: VirtualInput);
        await _plugin.InvokeAsync(ActionRulesMethods.Read, null, CancellationToken.None);
        await _plugin.Sweep!.Current;
        var c = new FakeDevice { Address = "10.0.0.5" };
        _devices.Items.Add(c);
        var reads = source.Requests.Count;

        var json = Json(new(RuleAgentAction.Copy, Change: new(Name: "Upload copy"), SourceDeviceId: _a.Id, SourceRuleId: id));
        Assert.Empty(Task.ValidatePayload(json, Caller));
        Assert.DoesNotContain("secret1", json, StringComparison.Ordinal);
        await RunAsync(_b, json);
        await RunAsync(c, json);

        foreach (var target in new[] { _b, c })
        {
            var rule = Assert.Single(Camera(target).Rules);
            Assert.Equal("Upload copy", rule.Value.Name);
            Assert.Equal("secret1", Camera(target).Param(rule.Key, "password"));
        }

        Assert.Equal(1, source.Requests.Skip(reads).Count(r => r == "SOAP GetActionRules")); // one read of the source for the run
        Assert.Equal("Copy action rule Upload copy", Assert.Single(await _plugin.Undo.ListAsync(CancellationToken.None)).Description);
    }
}
