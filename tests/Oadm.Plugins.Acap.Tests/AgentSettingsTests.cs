using System.Text.Json;

using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// Change settings from an AI agent (<see cref="AcapMcpPayload"/>, docs/spec/mcp.md): field errors before any task starts, the
/// plan from cached data, a password setting never written from an agent's payload, and the API check before the first write.
/// </summary>
public sealed class AgentSettingsTests
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private const string Secret = "s3cret-value";

    /// <summary>What an agent sends: the MCP record, read by the task as an <see cref="AcapPayload"/>.</summary>
    private static string Agent(Dictionary<string, string>? values, string? app = "demo") =>
        JsonSerializer.Serialize(new AcapMcpPayload(AcapMcpAction.Settings, app, Values: values), AcapPlugin.Json);

    private static (FakeAcapDevice Device, RecordingContext Ctx) Setup()
    {
        var device = new FakeAcapDevice();
        device.Add("demo", "1.0.0", "Running");
        device.AddSetting("demo", "Threshold", "<int min=\"0\" max=\"100\" />", "40", niceName: "Threshold");
        device.AddSetting("demo", "Server", "<string maxlen=\"64\" />", "10.0.0.5", niceName: "Server address");
        device.AddSetting("demo", "Shared", "<password maxlen=\"32\" />", "old", flags: "writeonly=\"true\"", niceName: "Shared key");
        return (device, new RecordingContext(device));
    }

    private static Task Run(RecordingContext ctx, string payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    private static Dictionary<string, string> Errors(string json) =>
        Plugin.ValidatePayload(json, Caller).ToDictionary(e => e.Field, e => e.Message, StringComparer.Ordinal);

    [Fact]
    public void The_agent_record_reads_as_the_settings_action_with_its_values_and_no_passwords()
    {
        var payload = AcapPayload.Parse(Agent(new() { ["Threshold"] = "55" }));

        Assert.Equal(AcapAction.Settings, payload.Action);
        Assert.Equal("demo", payload.Application);
        Assert.Equal("55", payload.Values!["Threshold"]);
        Assert.False(payload.PasswordsAllowed);
        Assert.Equal((int)AcapAction.Settings, (int)AcapMcpAction.Settings);
        Assert.Equal("Change settings of demo", Plugin.GetTaskName(Agent(new() { ["Threshold"] = "55" })));
        Assert.Equal([AcapPermissions.Settings], Plugin.RequiredPermissions(Agent(new() { ["Threshold"] = "55" })));
        Assert.Empty(Plugin.ValidatePayload(Agent(new() { ["Threshold"] = "55", ["Server"] = string.Empty }), Caller));
    }

    [Fact]
    public void The_record_has_no_field_for_license_key_files_or_passwords_allowed_and_its_secrets_are_no_input()
    {
        var names = typeof(AcapMcpPayload).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains(nameof(AcapMcpPayload.Values), names);
        Assert.DoesNotContain(nameof(AcapPayload.LicenseKeys), names);
        Assert.DoesNotContain(nameof(AcapPayload.PasswordsAllowed), names);
        Assert.Equal((int)AcapAction.InstallLicense, (int)AcapMcpAction.InstallLicense);
    }

    private static string AgentPasswords(Dictionary<string, string>? values, params string[] passwords) =>
        JsonSerializer.Serialize(new AcapMcpPayload(AcapMcpAction.Settings, "demo", Values: values, Passwords: [.. passwords.Select(n => new AcapPasswordSetting(n))]), AcapPlugin.Json);

    [Fact]
    public async Task A_password_setting_is_a_secret_the_person_enters_and_is_written_with_the_others()
    {
        var json = AgentPasswords(new() { ["Threshold"] = "55" }, "Shared");
        Assert.Equal([new Oadm.Sdk.Tasks.TaskSecret("password_0", "password for Shared", "passwords[0].value")], Plugin.SecretsOf(json));
        Assert.Equal(AcapPayload.EnterPassword, Assert.Single(Errors(json)).Value); // only its own field until it is entered
        Assert.Equal("passwords[0].value", Assert.Single(Errors(json)).Key);
        Assert.Equal([AcapPermissions.Settings], Plugin.RequiredPermissions(json));
        var plan = await Plugin.PlanAsync(json, [new FakeDevice()], CancellationToken.None);
        Assert.Equal([AcapTaskPlugin.PasswordsNote(["Shared"]), AcapTaskPlugin.SettingsCheckedOnDeviceWarning], plan.Warnings);

        // The run's payload has the value the person entered (the host puts it there).
        var entered = json.Replace("\"name\":\"Shared\"", "\"name\":\"Shared\",\"value\":\"" + Secret + "\"", StringComparison.Ordinal);
        Assert.Empty(Errors(entered));
        var (device, ctx) = Setup();
        await Run(ctx, entered);
        var form = Assert.Single(device.SettingsUpdates);
        Assert.Contains("root.demo.Shared=" + Secret, form, StringComparison.Ordinal);
        Assert.Contains("root.demo.Threshold=55", form, StringComparison.Ordinal);
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(StepRun.Lines(ctx.Steps), l => l.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, Plugin.GetTaskName(entered), StringComparison.Ordinal);

        // A password in values is still refused: only the person's entered value is written.
        Assert.Equal("Shared.Password is a password. List it in passwords: the user enters it.", Errors(Agent(new() { ["Shared.Password"] = Secret }))["values.Shared.Password"]);
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(AgentPasswords(new() { ["Shared"] = "x" }, "Shared"))); // the same name twice
    }

    [Fact]
    public async Task A_license_key_is_a_secret_the_person_enters_for_one_device()
    {
        var json = JsonSerializer.Serialize(new AcapMcpPayload(AcapMcpAction.InstallLicense, "ax_msf"), AcapPlugin.Json);
        Assert.Equal([new Oadm.Sdk.Tasks.TaskSecret(AcapTaskPlugin.LicenseKeySlot, "license key", AcapTaskPlugin.LicenseKeyField)], Plugin.SecretsOf(json));
        Assert.Equal(AcapPayload.EnterLicenseKey, Errors(json)[AcapTaskPlugin.LicenseKeyField]);
        Assert.Equal([AcapPermissions.License], Plugin.RequiredPermissions(json));
        var one = await Plugin.PlanAsync(json, [new FakeDevice()], CancellationToken.None);
        Assert.Null(Assert.Single(one.Devices).SkipReason);
        Assert.Equal([AcapTaskPlugin.LicenseKeyNote], one.Warnings);
        var two = await Plugin.PlanAsync(json, [new FakeDevice(), new FakeDevice { Serial = "B8A44F000002", Address = "10.0.0.49" }], CancellationToken.None);
        Assert.All(two.Devices, d => Assert.Equal(AcapTaskPlugin.OneDeviceForAKey, d.SkipReason));

        var bad = JsonSerializer.Serialize(new AcapMcpPayload(AcapMcpAction.InstallLicense, "ax_msf", LicenseKey: "not a key"), AcapPlugin.Json);
        Assert.Equal(AcapPayload.BadLicenseKey, Errors(bad)[AcapTaskPlugin.LicenseKeyField]);

        var entered = JsonSerializer.Serialize(new AcapMcpPayload(AcapMcpAction.InstallLicense, "ax_msf", LicenseKey: LicenseKeyTests.Key()), AcapPlugin.Json);
        Assert.Empty(Errors(entered));
        var device = new FakeAcapDevice();
        device.Add("ax_msf", "1.0", applicationId: "415090");
        var ctx = new RecordingContext(device);
        await Run(ctx, entered);
        Assert.Equal(LicenseKeyTests.Key(), device.LicenseKeys["ax_msf"]);
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains("Signature", StringComparison.Ordinal));
    }

    [Fact]
    public void Field_errors_name_the_setting_with_the_dialogs_texts_and_never_a_value()
    {
        Assert.Equal("Choose at least one setting to change.", Errors(Agent(null))["values"]);
        Assert.Equal("Choose at least one setting to change.", Errors(Agent([]))["values"]);

        var many = Enumerable.Range(0, AppSettings.MaxValues + 1).ToDictionary(i => "P" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "1");
        Assert.Equal("Change at most 200 settings at once.", Errors(Agent(many))["values"]);

        var errors = Errors(Agent(new()
        {
            ["Long"] = new string('x', AppSettings.MaxValueLength + 1),
            ["Line"] = "a\nb",
            ["Mqtt.Password"] = Secret,
            ["ApiToken"] = Secret,
            ["a&b"] = "1",
            ["Threshold"] = "55",
        }));
        Assert.Equal("Long must be at most 4,096 characters.", errors["values.Long"]);
        Assert.Equal("Line contains characters the device does not take.", errors["values.Line"]);
        Assert.Equal("Mqtt.Password is a password. List it in passwords: the user enters it.", errors["values.Mqtt.Password"]);
        Assert.Equal("ApiToken is a password. List it in passwords: the user enters it.", errors["values.ApiToken"]);
        Assert.Equal("A setting name has letters, digits and underscores only, with dots between parts.", errors["values"]);
        Assert.DoesNotContain("values.Threshold", errors.Keys);
        Assert.DoesNotContain(errors.Values, m => m.Contains(Secret, StringComparison.Ordinal));

        Assert.Equal("The application name is not valid. Nothing was changed.", Errors(Agent(new() { ["Threshold"] = "1" }, app: null))["application"]);
    }

    [Fact]
    public void The_dialog_may_set_a_password_name_an_agent_may_not()
    {
        var dialog = new AcapPayload { Action = AcapAction.Settings, Application = "demo", Values = new Dictionary<string, string> { ["Mqtt.Password"] = Secret }, PasswordsAllowed = true }.ToJson();

        Assert.Empty(Plugin.ValidatePayload(dialog, Caller));
        Assert.Single(Plugin.ValidatePayload(Agent(new() { ["Mqtt.Password"] = Secret }), Caller));
    }

    [Fact]
    public async Task A_setting_the_application_declares_as_a_password_fails_before_any_write()
    {
        var (device, ctx) = Setup();

        // "Shared" does not name a secret, so only the device's own definition shows it is a password.
        Assert.Empty(Plugin.ValidatePayload(Agent(new() { ["Shared"] = Secret, ["Threshold"] = "55" }), Caller));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Agent(new() { ["Shared"] = Secret, ["Threshold"] = "55" })));

        Assert.Equal("Shared key is a password. List it in passwords: the user enters it. Nothing was changed.", ex.Message);
        Assert.Empty(device.Writes);
        Assert.Empty(device.SettingsUpdates);
        Assert.Equal("Read settings: Failed", StepRun.Lines(ctx.Steps)[2]);
        Assert.DoesNotContain(ctx.Logs, l => l.Message.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agents_values_are_checked_on_the_device_and_written_in_one_request()
    {
        var (device, ctx) = Setup();

        await Run(ctx, Agent(new() { ["Threshold"] = "055", ["Server"] = "10.0.0.9" }));

        var form = Assert.Single(device.SettingsUpdates);
        Assert.Contains("root.demo.Threshold=55", form, StringComparison.Ordinal);
        Assert.Contains("root.demo.Server=10.0.0.9", form, StringComparison.Ordinal);
        Assert.Equal("2 settings changed", StepRun.Detail(ctx.Steps, "Verify settings"));
    }

    [Fact]
    public async Task An_invalid_payload_touches_no_device()
    {
        var (device, ctx) = Setup();

        await Assert.ThrowsAsync<ArgumentException>(() => Run(ctx, Agent([])));

        Assert.Empty(device.Requests);
        Assert.Equal(0, device.ApiListCalls);
    }

    [Fact]
    public async Task A_device_without_the_application_api_is_refused_before_any_write()
    {
        var (device, ctx) = Setup();
        device.Apis.RemoveAll(a => a.Id == AcapPlugin.ApplicationApiId);

        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => Run(ctx, Agent(new() { ["Threshold"] = "55" })));

        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(device.Writes);
        Assert.Empty(device.SettingsUpdates);
    }

    [Fact]
    public async Task The_plan_runs_where_the_api_is_cached_and_says_each_device_checks_the_values()
    {
        var ready = new FakeDevice { Address = "10.0.0.48" };
        var old = new FakeDevice { Address = "10.0.0.49", Apis = [] };
        var down = new FakeDevice { Address = "10.0.0.50", Status = Oadm.Sdk.Devices.DeviceStatus.Unreachable };

        var plan = await Plugin.PlanAsync(Agent(new() { ["Threshold"] = "55" }), [ready, old, down], new InMemoryUploadedFiles(), CancellationToken.None);

        Assert.Equal([ready.Id, old.Id, down.Id], plan.Devices.Select(d => d.DeviceId));
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.NotNull(plan.Devices[1].SkipReason);
        Assert.NotNull(plan.Devices[2].SkipReason);
        Assert.Equal([AcapTaskPlugin.SettingsCheckedOnDeviceWarning], plan.Warnings);
        Assert.Empty(plan.Files);
    }
}
