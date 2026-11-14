using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Tasks;
using Oadm.Plugins.Acap;
using Oadm.Plugins.Firmware;
using Oadm.Plugins.Network;
using Oadm.Plugins.Pki;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Plugins.Users;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Server.Mcp;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The rest of the task tools of stage 2 (docs/spec/mcp.md "Tasks", "Files"): the schemas, the checks and the plans of Upgrade
/// AXIS OS, Network, Applications and the PKI tasks; cancelling tasks; the files of upload links; download links.
/// </summary>
public sealed class McpTaskPartTests
{
    /// <summary>The Applications task (<c>AcapPlugin.Id</c>; HardeningScan links the same class, so the type name is ambiguous here).</summary>
    private const string AcapTaskId = "oadm.acap";

    private static readonly DeviceApi Fwmgr = new("fwmgr", "1.0");
    private static readonly DeviceApi NetworkSettings = new("network-settings", "1.0");
    private static readonly DeviceApi Application = new("application", "1.0");

    private static async Task<McpTestServer> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var plugins = server.Host.Get<PluginRegistry>();
        ITaskPlugin[] tasks =
        [
            new FirmwareTaskPlugin(), new NetworkSettingsTaskPlugin(), new AssignIpTaskPlugin(), new AcapTaskPlugin(), new UsersTaskPlugin(),
            new HttpsEnableTask(() => null), new HttpsDisableTask(() => null), new Dot1xEnableTask(() => null), new Dot1xDisableTask(() => null),
            new RenewTask(() => null), new InstallCaTask(() => null), new InstallCertificatesTask(() => null), new DeleteCertificatesTask(() => null),
            new ViewCertificatesTask(() => null),
        ];
        foreach (var task in tasks)
        {
            Assert.True(plugins.RegisterTaskPlugin(task, new PluginOrigin(task.Id, "1.0.0", null)), task.Id);
        }

        return server;
    }

    private static async Task<Device> AddAsync(TestServerHost host, string address, int serial, string model, string firmware, DeviceStatus status = DeviceStatus.Ok, params DeviceApi[] apis)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = DeviceScheme.Http, Status = status, Model = model, FirmwareVersion = firmware, Apis = apis },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pw", CancellationToken.None);
        return device;
    }

    private static JsonObject Filter(params Device[] devices) => new() { ["ids"] = new JsonArray([.. devices.Select(d => (JsonNode)d.Id.ToString())]) };

    private static async Task<JsonObject> PreviewAsync(McpTestServer server, string key, string task, JsonObject filter, JsonObject input) =>
        await server.ToolAsync(key, "oadm_task_preview", new JsonObject { ["task"] = task, ["filter"] = filter, ["input"] = input });

    private static async Task<Dictionary<string, string>> RefusedAsync(McpTestServer server, string key, string task, JsonObject filter, JsonObject input)
    {
        var result = await server.CallToolAsync(key, "oadm_task_preview", new JsonObject { ["task"] = task, ["filter"] = filter, ["input"] = input });
        Assert.True(result["isError"]!.GetValue<bool>());
        var answer = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        return answer["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheBundledTasksWithoutASecretHaveASchemaAndTheOthersStayNotOffered()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        var catalog = await server.ToolAsync(key, "oadm_tasks_catalog", new JsonObject());
        var tasks = catalog["tasks"]!.AsArray().Select(t => t!["task"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        string[] offered =
        [
            FirmwareTaskPlugin.PluginId, NetworkSettingsTaskPlugin.PluginId, AssignIpTaskPlugin.PluginId, AcapTaskId, PkiTaskIds.HttpsEnable, PkiTaskIds.HttpsDisable,
            PkiTaskIds.Dot1xEnable, PkiTaskIds.Dot1xDisable, PkiTaskIds.Renew, PkiTaskIds.InstallCa,
        ];
        Assert.All(offered, id => Assert.Contains(id, tasks));

        // A password (users, install certificates) or a choice only the dialog makes (what to delete, what to view) stays in OADM.
        string[] notOffered = [UsersTaskPlugin.PluginId, PkiTaskIds.Install, PkiTaskIds.Delete, PkiTaskIds.View];
        Assert.All(notOffered, id => Assert.DoesNotContain(id, tasks));
        Assert.All(notOffered, id => Assert.Null(TaskPayloadSchemas.Of(server.Host.Get<PluginRegistry>().TryGetTaskPlugin(id, out var plugin) ? plugin.Plugin : throw new InvalidOperationException(id))));

        var firmware = TaskPayloadSchemas.Of(new FirmwareTaskPlugin())!;
        Assert.Equal(["files"], firmware["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(["none", "soft", "hard"], firmware["properties"]!["factoryDefaultMode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.DoesNotContain("online", firmware["properties"]!.AsObject().Select(p => p.Key)); // axis.com needs the user's own sign-in

        var acap = TaskPayloadSchemas.Of(new AcapTaskPlugin())!;
        Assert.Equal(["install", "remove", "start", "stop"], acap["properties"]!["action"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.DoesNotContain(acap["properties"]!.AsObject().Select(p => p.Key), name => name is "licenseKeys" or "values"); // keys and settings can hold secrets

        var network = TaskPayloadSchemas.Of(new NetworkSettingsTaskPlugin())!;
        var devices = network["properties"]!["devices"]!;
        Assert.Equal("uuid", devices["propertyNames"]!["format"]!.GetValue<string>());
        Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, devices["maxProperties"]!.GetValue<int>());
        Assert.Equal("object", devices["additionalProperties"]!["type"]!.GetValue<string>());
        Assert.False(devices["additionalProperties"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UpgradeAxisOsPlansFromTheCachedModelAndVersionAndChecksTheFilesLikeTheDialog()
    {
        await using var server = await StartAsync();
        var up = await AddAsync(server.Host, "10.20.0.1", 5001, "P3265-V", "12.8.0", DeviceStatus.Ok, Fwmgr);
        var same = await AddAsync(server.Host, "10.20.0.2", 5002, "P3265-V", "12.11.77", DeviceStatus.Ok, Fwmgr);
        var other = await AddAsync(server.Host, "10.20.0.3", 5003, "M3085-V", "12.8.0", DeviceStatus.Ok, Fwmgr);
        var older = await AddAsync(server.Host, "10.20.0.4", 5004, "P3265-V", "12.12.0", DeviceStatus.Ok, Fwmgr);
        var down = await AddAsync(server.Host, "10.20.0.5", 5005, "P3265-V", "12.5.0", DeviceStatus.Unreachable, Fwmgr);
        var key = await server.CreateKeyAsync();
        var tasksBefore = (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count;
        var fileId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        JsonObject Input(string mode = "none", bool downgrade = false) => new()
        {
            ["files"] = new JsonArray(new JsonObject { ["fileId"] = fileId, ["fileName"] = "P3265-V_12_11_77.bin" }),
            ["factoryDefaultMode"] = mode,
            ["allowDowngrade"] = downgrade,
        };

        var preview = await PreviewAsync(server, key, FirmwareTaskPlugin.PluginId, Filter(up, same, other, older, down), Input());
        Assert.Equal(5, preview["devices"]!.GetValue<int>());
        Assert.Equal(1, preview["will_run"]!.GetValue<int>());
        Assert.Equal(up.Id.ToString(), Assert.Single(preview["first"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal("Upgrade AXIS OS to 12.11.77", preview["task_name"]!.GetValue<string>());
        var skipped = preview["skipped"]!.AsArray().Select(s => s!["reason"]!.GetValue<string>()).ToList();
        Assert.Contains(skipped, r => r.StartsWith("The file is for P3265-V, the device is M3085-V.", StringComparison.Ordinal)); // another model
        Assert.Contains(skipped, r => r.StartsWith("Already up to date", StringComparison.Ordinal));
        Assert.Contains(skipped, r => r.StartsWith("Downgrade 12.12.0", StringComparison.Ordinal) && r.Contains("Allow downgrade", StringComparison.Ordinal));
        Assert.Equal(4, skipped.Count); // and the unreachable one, by its own reason
        Assert.Empty(preview["warnings"]!.AsArray());
        Assert.False(string.IsNullOrEmpty(preview["handle"]!.GetValue<string>()));
        Assert.Equal(tasksBefore, (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count); // nothing written

        // A factory default is warned about with the dialog's text, and a downgrade with it is planned.
        var soft = await PreviewAsync(server, key, FirmwareTaskPlugin.PluginId, Filter(older), Input("soft", downgrade: true));
        Assert.Equal(1, soft["will_run"]!.GetValue<int>());
        Assert.Equal(FirmwareWarnings.Soft, Assert.Single(soft["warnings"]!.AsArray())!.GetValue<string>());

        // The dialog's rules: a file that is not a .bin file, at its field; the schema first for a name or a choice it does not know.
        var rules = await RefusedAsync(server, key, FirmwareTaskPlugin.PluginId, Filter(up), new JsonObject { ["files"] = new JsonArray(new JsonObject { ["fileId"] = fileId, ["fileName"] = "firmware.zip" }) });
        Assert.StartsWith("Not an AXIS OS image", rules["input.files[0].fileName"], StringComparison.Ordinal);
        var schema = await RefusedAsync(server, key, FirmwareTaskPlugin.PluginId, Filter(up), new JsonObject { ["files"] = new JsonArray(), ["factoryDefaultMode"] = "Soft", ["online"] = new JsonArray() });
        Assert.Equal(ToolInputTexts.Unknown, schema["input.online"]);
        Assert.StartsWith("Use one of: none, soft, hard", schema["input.factoryDefaultMode"], StringComparison.Ordinal);
        Assert.Equal("Use at least 1 values.", schema["input.files"]);
    }

    [Fact]
    public async Task NetworkChecksTheBatchLikeTheDialogAndWarnsBeforeAnAddressChanges()
    {
        await using var server = await StartAsync();
        var a = await AddAsync(server.Host, "10.21.0.1", 5101, "M3085-V", "12.8.0", DeviceStatus.Ok, NetworkSettings);
        var b = await AddAsync(server.Host, "10.21.0.2", 5102, "M3085-V", "12.8.0", DeviceStatus.Ok, NetworkSettings);
        var c = await AddAsync(server.Host, "10.21.0.3", 5103, "M3085-V", "12.8.0", DeviceStatus.Ok, NetworkSettings);
        var gone = await AddAsync(server.Host, "10.21.0.4", 5104, "M3085-V", "12.8.0", DeviceStatus.Unreachable, NetworkSettings);
        var key = await server.CreateKeyAsync();
        JsonObject Static(string first, string second) => new()
        {
            ["ipv4"] = new JsonObject { ["mode"] = "static", ["prefixLength"] = 24, ["gateway"] = "10.21.0.254" },
            ["devices"] = new JsonObject
            {
                [a.Id.ToString()] = new JsonObject { ["ipv4Address"] = first },
                [b.Id.ToString()] = new JsonObject { ["ipv4Address"] = second },
            },
        };

        // c is not listed in the settings and gone is unreachable: only a and b run; the move and the mask are warned about.
        var preview = await PreviewAsync(server, key, NetworkSettingsTaskPlugin.PluginId, Filter(a, b, c, gone), Static("10.21.0.11", "10.21.0.12"));
        Assert.Equal(4, preview["devices"]!.GetValue<int>());
        Assert.Equal(2, preview["will_run"]!.GetValue<int>());
        Assert.Equal(2, preview["skipped"]!.AsArray().Sum(s => s!["count"]!.GetValue<int>()));
        var warnings = preview["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains("2 of 2 devices get a new IP address. OADM follows them.", warnings);
        Assert.Contains(Oadm.Plugins.Network.Model.NetworkWarningTexts.MaskAndRouter, warnings);
        Assert.False(string.IsNullOrWhiteSpace(preview["task_name"]!.GetValue<string>()));

        // The dialog's texts, at the JSON field: a duplicate address, a router in another subnet.
        var twice = await RefusedAsync(server, key, NetworkSettingsTaskPlugin.PluginId, Filter(a, b), Static("10.21.0.11", "10.21.0.11"));
        Assert.Equal("IPv4: 10.21.0.11 is assigned to 2 devices.", twice["input.ipv4"]);
        var dns = await RefusedAsync(server, key, NetworkSettingsTaskPlugin.PluginId, Filter(a), new JsonObject
        {
            ["dns"] = new JsonObject { ["useDhcp"] = false, ["servers"] = new JsonArray("10.0.0.1", "not an address") },
            ["devices"] = new JsonObject { [a.Id.ToString()] = new JsonObject() },
        });
        Assert.Equal("DNS: \"not an address\" is not a valid IP address.", dns["input.dns"]);
        var noDevices = await RefusedAsync(server, key, NetworkSettingsTaskPlugin.PluginId, Filter(a), new JsonObject { ["dns"] = new JsonObject { ["useDhcp"] = true } });
        Assert.Equal("No devices selected.", noDevices["input.devices"]);

        // The map checks its names and values by the schema.
        var names = await RefusedAsync(server, key, NetworkSettingsTaskPlugin.PluginId, Filter(a), new JsonObject
        {
            ["dns"] = new JsonObject { ["useDhcp"] = true },
            ["devices"] = new JsonObject { ["not-an-id"] = new JsonObject(), [a.Id.ToString()] = new JsonObject { ["colour"] = "red" } },
        });
        Assert.Equal(ToolInputTexts.Id, names["input.devices.not-an-id"]);
        Assert.Equal(ToolInputTexts.Unknown, names[$"input.devices.{a.Id}.colour"]);

        // Assign IP address: IPv4 and the devices; DHCP is warned about; IPv6 and host names are not in its input.
        var dhcp = await PreviewAsync(server, key, AssignIpTaskPlugin.PluginId, Filter(a, b), new JsonObject
        {
            ["ipv4"] = new JsonObject { ["mode"] = "dhcp" },
            ["devices"] = new JsonObject { [a.Id.ToString()] = new JsonObject(), [b.Id.ToString()] = new JsonObject() },
        });
        Assert.Equal(2, dhcp["will_run"]!.GetValue<int>());
        Assert.Equal(Oadm.Plugins.Network.Model.NetworkWarningTexts.Dhcp, Assert.Single(dhcp["warnings"]!.AsArray())!.GetValue<string>());
        var assign = await RefusedAsync(server, key, AssignIpTaskPlugin.PluginId, Filter(a), new JsonObject
        {
            ["ipv4"] = new JsonObject { ["mode"] = "dhcp" },
            ["devices"] = new JsonObject { [a.Id.ToString()] = new JsonObject() },
            ["hostName"] = new JsonObject { ["useDhcp"] = true },
        });
        Assert.Equal(ToolInputTexts.Unknown, assign["input.hostName"]);
    }

    [Fact]
    public async Task ApplicationsTakesPackagesAndNamesButNeitherLicenseKeysNorSettings()
    {
        await using var server = await StartAsync();
        var device = await AddAsync(server.Host, "10.22.0.1", 5201, "M3085-V", "12.8.0", DeviceStatus.Ok, Application);
        var key = await server.CreateKeyAsync();

        var remove = await PreviewAsync(server, key, AcapTaskId, Filter(device), new JsonObject { ["action"] = "remove", ["application"] = "vmd" });
        Assert.Equal(1, remove["will_run"]!.GetValue<int>());
        Assert.Equal("Remove vmd", remove["task_name"]!.GetValue<string>());
        Assert.Equal("The application's settings are lost with it.", Assert.Single(remove["warnings"]!.AsArray())!.GetValue<string>());
        var stop = await PreviewAsync(server, key, AcapTaskId, Filter(device), new JsonObject { ["action"] = "stop", ["application"] = "vmd" });
        Assert.Empty(stop["warnings"]!.AsArray());

        // The dialog's rules at their fields; the schema leaves license keys and settings out.
        var install = await RefusedAsync(server, key, AcapTaskId, Filter(device), new JsonObject { ["action"] = "install" });
        Assert.StartsWith("Install needs an uploaded package", install["input.files"], StringComparison.Ordinal);
        var name = await RefusedAsync(server, key, AcapTaskId, Filter(device), new JsonObject { ["action"] = "start", ["application"] = "../x" });
        Assert.StartsWith("The application name is not valid", name["input.application"], StringComparison.Ordinal);
        var secret = await RefusedAsync(server, key, AcapTaskId, Filter(device), new JsonObject
        {
            ["action"] = "installLicense",
            ["licenseKeys"] = new JsonArray(),
            ["values"] = new JsonObject { ["a"] = "b" },
        });
        Assert.StartsWith("Use one of: install, remove, start, stop", secret["input.action"], StringComparison.Ordinal);
        Assert.Equal(ToolInputTexts.Unknown, secret["input.licenseKeys"]);
        Assert.Equal(ToolInputTexts.Unknown, secret["input.values"]);
    }

    [Fact]
    public async Task ThePkiTasksWarnLikeTheDialogsAndRefuseWithoutTheirPolicy()
    {
        await using var server = await StartAsync();
        var device = await AddAsync(server.Host, "10.23.0.1", 5301, "M3085-V", "12.8.0");
        var old = await AddAsync(server.Host, "10.23.0.2", 5302, "M3085-V", "6.20.1");
        var key = await server.CreateKeyAsync();

        var disable = await PreviewAsync(server, key, PkiTaskIds.HttpsDisable, Filter(device, old), new JsonObject());
        Assert.Equal(1, disable["will_run"]!.GetValue<int>()); // the old firmware has no certificate API
        Assert.Equal(PkiTaskIds.HttpsDisableWarning, Assert.Single(disable["warnings"]!.AsArray())!.GetValue<string>());
        var dot1x = await PreviewAsync(server, key, PkiTaskIds.Dot1xDisable, Filter(device), new JsonObject());
        Assert.Empty(dot1x["warnings"]!.AsArray());

        // No PKI service here: an issuing task says so before any task starts; with the clock question left out of the input.
        var enable = await RefusedAsync(server, key, PkiTaskIds.HttpsEnable, Filter(device), new JsonObject { ["policyId"] = Guid.NewGuid().ToString() });
        Assert.Equal("The PKI plugin is not running.", enable["input"]);
        var clock = await RefusedAsync(server, key, PkiTaskIds.Dot1xEnable, Filter(device), new JsonObject { ["clockConfirmed"] = true });
        Assert.Equal(ToolInputTexts.Unknown, clock["input.clockConfirmed"]);

        var ca = await RefusedAsync(server, key, PkiTaskIds.InstallCa, Filter(device), new JsonObject
        {
            ["certificates"] = new JsonArray(new JsonObject { ["name"] = "Acme", ["pem"] = "not a certificate" }),
        });
        Assert.StartsWith("Acme:", ca["input.certificates"], StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", ca["input.certificates"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellingAPreviewCountsWhatStopsAndRunsOnceForItsKeyWithoutTouchingFinishedTasks()
    {
        await using var server = await McpTestServer.StartAsync();
        var plugins = server.Host.Get<PluginRegistry>();
        var blocking = new BlockingTask();
        Assert.True(plugins.RegisterTaskPlugin(blocking, new PluginOrigin(BlockingTask.PluginId, "1.0.0", null)));
        Assert.True(plugins.RegisterTaskPlugin(new QuickTask(), new PluginOrigin(QuickTask.PluginId, "1.0.0", null)));
        var a = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.24.0.1", 5401);
        var b = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.24.0.2", 5402);
        var key = await server.CreateKeyAsync();
        var engine = server.Host.Get<TaskEngine>();

        var run = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = BlockingTask.PluginId, DeviceIds = { a.Id.ToString(), b.Id.ToString() } });
        var quick = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = QuickTask.PluginId, DeviceIds = { a.Id.ToString() } });
        await engine.WaitForCompletionAsync(Guid.Parse(quick.TaskIds[0]), CancellationToken.None);

        // Exactly one of batch and task ids.
        var neither = await server.CallToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject());
        Assert.True(neither["isError"]!.GetValue<bool>());
        var both = await server.CallToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["batch"] = run.BatchId, ["task_ids"] = new JsonArray(run.TaskIds[0]) });
        Assert.True(both["isError"]!.GetValue<bool>());

        // The preview writes nothing: its two tasks wait or run, the finished one cannot be cancelled, an unknown id is counted.
        var ids = new JsonArray([.. run.TaskIds.Append(quick.TaskIds[0]).Append(Guid.NewGuid().ToString()).Select(i => (JsonNode)i)]);
        var preview = await server.ToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["task_ids"] = ids });
        Assert.Equal(3, preview["matched"]!.GetValue<int>());
        Assert.Equal(2, preview["will_cancel"]!.GetValue<int>());
        var cannot = preview["cannot"]!.AsArray().ToDictionary(c => c!["reason"]!.GetValue<string>(), c => c!["count"]!.GetValue<int>());
        Assert.Equal(1, cannot["Already Done."]);
        Assert.Equal(1, cannot["The task no longer exists."]);
        Assert.Equal(2, preview["first"]!.AsArray().Count);
        var stillActive = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = run.BatchId });
        Assert.Equal(0, stillActive["counts"]!["Cancelled"]!.GetValue<int>());

        // The handle belongs to its key, to its own run tool, and runs once.
        var handle = preview["handle"]!.GetValue<string>();
        var other = await server.CreateKeyAsync(name: "other");
        Assert.True((await server.CallToolAsync(other, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_files_delete_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        var cancelled = await server.ToolAsync(key, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(2, cancelled["cancelled"]!.GetValue<int>());
        var again = await server.CallToolAsync(key, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());
        Assert.Contains(TaskRunTools.CancelEnded, again["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        foreach (var id in run.TaskIds)
        {
            await engine.WaitForCompletionAsync(Guid.Parse(id), CancellationToken.None);
        }

        var after = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = run.BatchId });
        Assert.Equal(2, after["counts"]!["Cancelled"]!.GetValue<int>());
    }

    [Fact]
    public async Task CancellingByBatchFindsOnlyTheBatchAndAReadOnlyKeyCannotRunACancelOrADelete()
    {
        await using var server = await McpTestServer.StartAsync();
        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(new BlockingTask(), new PluginOrigin(BlockingTask.PluginId, "1.0.0", null)));
        var a = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.25.0.1", 5501);
        var key = await server.CreateKeyAsync();
        var run = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = BlockingTask.PluginId, DeviceIds = { a.Id.ToString() } });

        var preview = await server.ToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["batch"] = run.BatchId });
        Assert.Equal(1, preview["will_cancel"]!.GetValue<int>());
        Assert.Equal(a.Id.ToString(), Assert.Single(preview["first"]!.AsArray())!["device_id"]!.GetValue<string>());

        // A batch that does not exist matches nothing and answers no handle.
        var none = await server.ToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["batch"] = Guid.NewGuid().ToString() });
        Assert.Equal(0, none["matched"]!.GetValue<int>());
        Assert.Null(none["handle"]);

        // A Read only key lists no cancel run and no file delete run; the previews stay (they write nothing).
        var readOnly = await server.CreateKeyAsync(readOnly: true, name: "reader");
        var names = await server.ToolNamesAsync(readOnly);
        Assert.Contains("oadm_tasks_cancel_preview", names);
        Assert.Contains("oadm_files_delete_preview", names);
        Assert.Contains("oadm_files_list", names);
        Assert.DoesNotContain("oadm_tasks_cancel_run", names);
        Assert.DoesNotContain("oadm_files_delete_run", names);
        var cancelHandle = preview["handle"]!.GetValue<string>();
        var refused = await server.CallToolAsync(readOnly, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = cancelHandle });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains(McpAccess.ReadOnlyKey, refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        var cleanup = await server.ToolAsync(key, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = cancelHandle });
        Assert.Equal(1, cleanup["cancelled"]!.GetValue<int>());
    }

    [Fact]
    public async Task ACancelOfAGroupUserSeesOnlyTheTasksOfTheirDevicesAndTheOthersAnswerLikeUnknownOnes()
    {
        await using var server = await McpTestServer.StartAsync();
        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(new BlockingTask(), new PluginOrigin(BlockingTask.PluginId, "1.0.0", null)));
        var inside = await GroupCallers.DeviceAsync(server.Host, 31, "cancel-group");
        var outside = await GroupCallers.DeviceAsync(server.Host, 32);
        var group = await GroupCallers.ForTagAsync(server.Host, "grouper", "cancel-group");
        var key = await server.CreateKeyAsync(group.Invoker, name: "group-key");
        var run = await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest { PluginId = BlockingTask.PluginId, DeviceIds = { inside.Id.ToString(), outside.Id.ToString() } });

        var byBatch = await server.ToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["batch"] = run.BatchId });
        Assert.Equal(1, byBatch["will_cancel"]!.GetValue<int>());
        Assert.Equal(inside.Id.ToString(), Assert.Single(byBatch["first"]!.AsArray())!["device_id"]!.GetValue<string>());
        var byIds = await server.ToolAsync(key, "oadm_tasks_cancel_preview", new JsonObject { ["task_ids"] = new JsonArray([.. run.TaskIds.Select(i => (JsonNode)i)]) });
        Assert.Equal(1, byIds["matched"]!.GetValue<int>());
        Assert.Equal(1, Assert.Single(byIds["cannot"]!.AsArray())!["count"]!.GetValue<int>()); // the other group's task: "no longer exists"

        var cancelled = await server.ToolAsync(key, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = byBatch["handle"]!.GetValue<string>() });
        Assert.Equal(1, cancelled["cancelled"]!.GetValue<int>());
        var engine = server.Host.Get<TaskEngine>();
        var records = new Dictionary<Guid, Guid>(); // device -> task
        foreach (var id in run.TaskIds.Select(Guid.Parse))
        {
            records[(await engine.GetAsync(id, CancellationToken.None))!.DeviceId] = id;
        }

        await engine.WaitForCompletionAsync(records[inside.Id], CancellationToken.None);
        var outsideTask = records[outside.Id];
        Assert.NotEqual(TaskState.Cancelled, (await engine.GetAsync(outsideTask, CancellationToken.None))!.State); // not touched
        engine.Cancel(outsideTask);
        await engine.WaitForCompletionAsync(outsideTask, CancellationToken.None);
    }

    [Fact]
    public async Task TheFilesOfAKeyAreListedAndDeletedByPreviewAndRunButNeverAnotherKeysFiles()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "other");
        var mine = await UploadAsync(server, key, "first.bin", [1, 2, 3]);
        var second = await UploadAsync(server, key, "second.bin", [4, 5, 6, 7]);
        var foreign = await UploadAsync(server, other, "theirs.bin", [9]);

        var list = await server.ToolAsync(key, "oadm_files_list", new JsonObject());
        Assert.Equal(2, list["total"]!.GetValue<int>());
        Assert.Equal([second, mine], list["files"]!.AsArray().Select(f => f!["file_id"]!.GetValue<string>()));
        Assert.Equal(["second.bin", "first.bin"], list["files"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()));
        Assert.Equal(Convert.ToHexString(SHA256.HashData([4, 5, 6, 7])), list["files"]![0]!["sha256"]!.GetValue<string>(), ignoreCase: true);
        var page = await server.ToolAsync(key, "oadm_files_list", new JsonObject { ["limit"] = 1 });
        Assert.Equal(second, Assert.Single(page["files"]!.AsArray())!["file_id"]!.GetValue<string>());
        var next = await server.ToolAsync(key, "oadm_files_list", new JsonObject { ["limit"] = 1, ["cursor"] = page["next_cursor"]!.GetValue<string>() });
        Assert.Equal(mine, Assert.Single(next["files"]!.AsArray())!["file_id"]!.GetValue<string>());
        Assert.Null(next["next_cursor"]);

        // Another key's file answers like an unknown one, and nothing is fixed by a refused preview.
        var unknown = await server.CallToolAsync(key, "oadm_files_delete_preview", new JsonObject { ["file_ids"] = new JsonArray(foreign) });
        Assert.True(unknown["isError"]!.GetValue<bool>());
        Assert.Contains(FileTools.NoSuchFile, unknown["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_files_delete_preview", new JsonObject { ["file_ids"] = new JsonArray(mine, mine) });
        Assert.Equal(mine, Assert.Single(preview["files"]!.AsArray())!["file_id"]!.GetValue<string>());
        Assert.Equal(3, preview["bytes"]!.GetValue<long>());
        Assert.Equal(2, (await server.ToolAsync(key, "oadm_files_list", new JsonObject()))["total"]!.GetValue<int>()); // a preview deletes nothing

        var handle = preview["handle"]!.GetValue<string>();
        Assert.True((await server.CallToolAsync(other, "oadm_files_delete_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_tasks_cancel_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>()); // another run tool
        var run = await server.ToolAsync(key, "oadm_files_delete_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(1, run["deleted"]!.GetValue<int>());
        Assert.Equal(0, run["already_gone"]!.GetValue<int>());
        Assert.True((await server.CallToolAsync(key, "oadm_files_delete_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>()); // once
        Assert.Equal([second], (await server.ToolAsync(key, "oadm_files_list", new JsonObject()))["files"]!.AsArray().Select(f => f!["file_id"]!.GetValue<string>()));
        Assert.Equal(1, (await server.ToolAsync(other, "oadm_files_list", new JsonObject()))["total"]!.GetValue<int>()); // theirs is untouched
    }

    [Fact]
    public async Task ADownloadLinkServesItsFileOnceToItsKeyAndAReadOnlyKeyMayFetchItAndItIsAudited()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var readOnly = await server.CreateKeyAsync(name: "reader", readOnly: true);
        var other = await server.CreateKeyAsync(name: "other");
        byte[] file = [.. Enumerable.Range(0, 300 * 1024).Select(i => (byte)(i * 7))]; // more than one chunk
        var handles = server.Host.Get<ToolHandles>();
        var caller = (await server.Host.Get<McpAccess>().AuthenticateAsync(readOnly, "local", CancellationToken.None))!;
        var token = handles.AddDownload(caller.BindingKey, new PlannedDownload("report.csv", "text/csv", _ => Task.FromResult<Stream>(new MemoryStream(file))));

        using (var foreign = await GetAsync(server, other, token))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, foreign.StatusCode); // another key's link answers like an unknown one
        }

        using (var anonymous = await GetAsync(server, "not-a-key", token))
        {
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var got = await GetAsync(server, readOnly, token))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, got.StatusCode);
            Assert.Equal("text/csv", got.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", got.Content.Headers.ContentDisposition!.DispositionType);
            Assert.Equal("report.csv", got.Content.Headers.ContentDisposition!.FileName);
            Assert.Equal(file, await got.Content.ReadAsByteArrayAsync());
        }

        using (var twice = await GetAsync(server, readOnly, token))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, twice.StatusCode);
        }

        // A Read only key still sends no file, and a link of /files/ takes only GET and PUT.
        using (var post = new HttpRequestMessage(HttpMethod.Post, "/files/" + token))
        {
            using var client = server.Http();
            post.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var refused = await client.SendAsync(post);
            Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, refused.StatusCode);
        }

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.FileDownloaded && e.Target == "report.csv" && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "reader"));
    }

    private static async Task<string> UploadAsync(McpTestServer server, string key, string name, byte[] content)
    {
        var link = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = name, ["size"] = content.Length });
        using var client = server.Http();
        using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(link["url"]!.GetValue<string>()).PathAndQuery) { Content = new ByteArrayContent(content) };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var sent = await client.SendAsync(put);
        Assert.Equal(System.Net.HttpStatusCode.OK, sent.StatusCode);
        return JsonNode.Parse(await sent.Content.ReadAsStringAsync())!["file_id"]!.GetValue<string>();
    }

    private static async Task<HttpResponseMessage> GetAsync(McpTestServer server, string key, string token)
    {
        using var client = server.Http();
        using var get = new HttpRequestMessage(HttpMethod.Get, McpEndpoint.FilesPath + token);
        get.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return await client.SendAsync(get);
    }

    /// <summary>A task that waits until it is cancelled (no timing: it never ends by itself).</summary>
    private sealed class BlockingTask : ITaskPlugin
    {
        public const string PluginId = "test.blocking";

        public string Id => PluginId;

        public string DisplayName => "Waits";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    /// <summary>A task that is done at once.</summary>
    private sealed class QuickTask : ITaskPlugin
    {
        public const string PluginId = "test.quick";

        public string Id => PluginId;

        public string DisplayName => "Quick";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => false;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }
}
