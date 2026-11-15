using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// The SD card task for AI agents (docs/spec/mcp.md "Tasks"): its input, the page's rules as field errors, the plan from cached
/// data, the page's rights of a run outside the page, and that a bad input touches no camera.
/// </summary>
public sealed class PayloadTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static StorageTaskPlugin Task() => new(null, pollInterval: TimeSpan.FromMilliseconds(1));

    private static string Json(StorageMcpPayload payload) => EdgeStorageJson.Serialize(payload);

    [Theory]
    [InlineData(StorageActions.Check, null)]
    [InlineData(StorageActions.Repair, null)]
    [InlineData(StorageActions.Format, null)]
    [InlineData(StorageActions.EncryptionOff, null)]
    [InlineData(StorageActions.Retention, 0)]
    [InlineData(StorageActions.Retention, 30)]
    [InlineData(StorageActions.Retention, 7000)]
    public void Every_payload_the_page_sends_passes_except_encrypt(string action, int? days)
    {
        // exactly what EdgeStoragePlugin.RunAsync hands the task runner
        var json = EdgeStorageJson.Serialize(new TaskPayload(action, days));
        Assert.Empty(Task().ValidatePayload(json, Caller));
    }

    [Fact]
    public void Encrypt_is_refused_at_its_action_because_it_needs_a_passphrase()
    {
        var json = EdgeStorageJson.Serialize(new TaskPayload(StorageActions.EncryptionOn, Passphrase: "secret phrase"));
        var error = Assert.Single(Task().ValidatePayload(json, Caller));
        Assert.Equal(("action", "Encrypt on the Edge storage page. It needs a passphrase."), (error.Field, error.Message));
    }

    [Fact]
    public void The_agents_input_names_the_actions_as_the_task_reads_them()
    {
        foreach (var (action, text) in new[]
                 {
                     (StorageMcpAction.Check, "check"), (StorageMcpAction.Repair, "repair"), (StorageMcpAction.Retention, "retention"),
                     (StorageMcpAction.Format, "format"), (StorageMcpAction.EncryptionOff, "encryptionOff"),
                 })
        {
            var json = Json(new StorageMcpPayload(action, action == StorageMcpAction.Retention ? 14 : null));
            Assert.Equal(text, EdgeStorageJson.Deserialize<TaskPayload>(json).Action);
            Assert.Empty(Task().ValidatePayload(json, Caller));
        }

        var task = Task();
        Assert.Equal(typeof(StorageMcpPayload), task.PayloadType);
        var schema = EdgeStorageJson.Options.GetJsonSchemaAsNode(typeof(StorageMcpPayload), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        var properties = schema["properties"]!.AsObject();
        Assert.Equal(["action", "retentionDays"], properties.Select(p => p.Key));
        Assert.Equal(["check", "repair", "retention", "format", "encryptionOff"], properties["action"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.DoesNotContain("passphrase", schema.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"action":"retention"}""", "retentionDays", "Enter 0 to 7,000 days.")]
    [InlineData("""{"action":"retention","retentionDays":7001}""", "retentionDays", "Enter 0 to 7,000 days.")]
    [InlineData("""{"action":"retention","retentionDays":-1}""", "retentionDays", "Enter 0 to 7,000 days.")]
    [InlineData("""{"action":"wipe"}""", "action", "Unknown action.")]
    [InlineData("""{}""", "action", "Unknown action.")]
    [InlineData("""{ "action": """, "", "The request cannot be read.")]
    public void A_bad_input_has_its_field_and_the_pages_text(string json, string field, string message)
    {
        var error = Assert.Single(Task().ValidatePayload(json, Caller));
        Assert.Equal((field, message), (error.Field, error.Message));
    }

    [Fact]
    public async Task A_bad_input_from_an_agent_reaches_no_camera()
    {
        var camera = new SdCamera();
        var ctx = new RecordingContext(camera);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Task().ExecuteAsync(ctx, new FakeDevice(), """{"action":"retention","retentionDays":7001}""", CancellationToken.None));
        Assert.Equal("Enter 0 to 7,000 days. Nothing was changed.", ex.Message);
        Assert.Empty(camera.Requests);
    }

    [Fact]
    public async Task The_agents_format_runs_like_the_pages()
    {
        var camera = new SdCamera();
        var ctx = new RecordingContext(camera);
        await StepRun.RunAsync(ctx.Steps, () => Task().ExecuteAsync(ctx, new FakeDevice(), Json(new StorageMcpPayload(StorageMcpAction.Format)), CancellationToken.None));
        Assert.Contains("Format the SD card: Done", StepRun.Lines(ctx.Steps));
        Assert.True(camera.Mounted);
    }

    [Fact]
    public async Task A_camera_without_the_API_is_refused_before_any_write()
    {
        var camera = new SdCamera { Apis = [] };
        var ctx = new RecordingContext(camera);
        var ex = await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => StepRun.RunAsync(ctx.Steps, () => Task().ExecuteAsync(ctx, new FakeDevice(), Json(new StorageMcpPayload(StorageMcpAction.EncryptionOff)), CancellationToken.None)));
        Assert.Contains("Nothing was changed", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(camera.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_plan_skips_cameras_without_a_slot_or_a_login_and_says_what_is_lost()
    {
        var ok = new FakeDevice { Address = "10.0.0.48" };
        var speaker = new FakeDevice { Address = "10.0.0.60", Apis = [] };
        var locked = new FakeDevice { Address = "10.0.0.52", Status = DeviceStatus.CredentialsRequired };
        IReadOnlyList<IDeviceInfo> devices = [ok, speaker, locked];

        var format = await Task().PlanAsync(Json(new StorageMcpPayload(StorageMcpAction.Format)), devices, CancellationToken.None);
        Assert.Equal([ok.Id, speaker.Id, locked.Id], format.Devices.Select(d => d.DeviceId));
        Assert.Null(format.Devices[0].SkipReason);
        Assert.Equal(StorageTexts.NoApi, format.Devices[1].SkipReason);
        Assert.NotNull(format.Devices[2].SkipReason);
        Assert.Equal(["The SD cards are formatted: every recording on them is deleted.", "Recording stops until the task is finished."], format.Warnings);

        var off = await Task().PlanAsync(Json(new StorageMcpPayload(StorageMcpAction.EncryptionOff)), devices, CancellationToken.None);
        Assert.Contains(StoragePayloadRules.DeletesRecordings, off.Warnings);
        var repair = await Task().PlanAsync(Json(new StorageMcpPayload(StorageMcpAction.Repair)), devices, CancellationToken.None);
        Assert.Equal(["Damaged recordings can be lost.", "Recording stops until the task is finished."], repair.Warnings);
        var check = await Task().PlanAsync(Json(new StorageMcpPayload(StorageMcpAction.Check)), devices, CancellationToken.None);
        Assert.Equal(["Recording stops until the task is finished."], check.Warnings);
        var retention = await Task().PlanAsync(Json(new StorageMcpPayload(StorageMcpAction.Retention, 30)), devices, CancellationToken.None);
        Assert.Empty(retention.Warnings);
        Assert.Equal("Set SD card retention to 30 days", Task().GetTaskName(Json(new StorageMcpPayload(StorageMcpAction.Retention, 30))));
    }

    [Theory]
    [InlineData("""{"action":"check"}""", new[] { EdgeStorageMethods.ManagePermission })]
    [InlineData("""{"action":"retention","retentionDays":3}""", new[] { EdgeStorageMethods.ManagePermission })]
    [InlineData("""{"action":"repair"}""", new[] { EdgeStorageMethods.FormatPermission })]
    [InlineData("""{"action":"format"}""", new[] { EdgeStorageMethods.FormatPermission })]
    [InlineData("""{"action":"encryptionOff"}""", new[] { EdgeStorageMethods.FormatPermission })]
    [InlineData("""{"action":"encryptionOn"}""", new[] { EdgeStorageMethods.FormatPermission })]
    [InlineData("""{"action":"wipe"}""", new[] { EdgeStorageMethods.ManagePermission, EdgeStorageMethods.FormatPermission })]
    [InlineData("""{ "action": """, new[] { EdgeStorageMethods.ManagePermission, EdgeStorageMethods.FormatPermission })]
    public void A_run_outside_the_page_needs_the_pages_right_of_its_action(string json, string[] keys)
    {
        var task = Task();
        Assert.Equal(keys, task.PagePermissions(json));
        Assert.Empty(((ITaskPlugin)task).Permissions); // no task rights of its own: the page's rights decide
        Assert.False(task.ShowInMenus);
    }

    [Fact]
    public void The_page_and_the_agents_share_the_rights_of_the_core_plugin()
    {
        var keys = new EdgeStoragePlugin().Permissions.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(EdgeStorageMethods.ManagePermission, keys);
        Assert.Contains(EdgeStorageMethods.FormatPermission, keys);
    }
}
