extern alias actionrules;

using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Plugins.DeviceConfig;
using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Plugins.DeviceConfig.Tools;
using Oadm.Plugins.EdgeStorage;
using Oadm.Plugins.Pki;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;
using Rules = actionrules::Oadm.Plugins.ActionRules;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The task plugins that got an agent input on 2026-11-15 (docs/spec/mcp.md "Stage 2 as built", "Offered tasks"): the generic
/// task tools offer them with their schemas, plans and permissions, also the page tasks of Edge storage and the Action Rule
/// Editor (<see cref="ITaskPlugin.PagePermissions"/>), which never become menu entries. The plugins' own rules are tested in
/// their test projects.
/// </summary>
public sealed class McpAgentTaskInputTests
{
    private static readonly DeviceApi DiskManagement = new("disk-management", "1.0");

    private static async Task<McpTestServer> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        ICorePlugin[] cores = [new EdgeStoragePlugin(), new Rules::ActionRulesPlugin()];
        foreach (var plugin in cores)
        {
            registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
        }

        ITaskPlugin[] tasks = [new DeleteCertificatesTask(() => null), new ViewCertificatesTask(() => null)];
        foreach (var task in tasks)
        {
            Assert.True(registry.RegisterTaskPlugin(task, new PluginOrigin(task.Id, "1.0.0", null)), task.Id);
        }

        var deviceConfig = new PluginOrigin(DeviceConfigIds.PackageId, "1.0.0", null) { DisplayName = "Device configuration", AlwaysOn = true };
        registry.RegisterCorePlugin(new DeviceConfigToolsPlugin(), deviceConfig);
        foreach (var task in (ITaskPlugin[])[new ConfigureDevicesTask(), new CreateConfigFileTask(), new ExportConfigTask(), new ImportConfigTask()])
        {
            Assert.True(registry.RegisterTaskPlugin(task, deviceConfig), task.Id);
        }

        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return server;
    }

    private static async Task<Device> AddAsync(TestServerHost host, string address, int serial, params DeviceApi[] apis)
    {
        var device = await host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(serial), Address = address, Scheme = DeviceScheme.Http, Status = DeviceStatus.Ok, Model = "M3085-V", FirmwareVersion = "12.8.0", Apis = apis },
            CancellationToken.None);
        await host.Get<CredentialStore>().SetAsync(device.Id, "root", "pw", CancellationToken.None);
        return device;
    }

    private static JsonObject Filter(params Device[] devices) => new() { ["ids"] = new JsonArray([.. devices.Select(d => (JsonNode)d.Id.ToString())]) };

    private static JsonObject Preview(string task, JsonObject filter, JsonObject input) => new() { ["task"] = task, ["filter"] = filter, ["input"] = input };

    private static Dictionary<string, string> Errors(JsonObject result)
    {
        Assert.True(result["isError"]!.GetValue<bool>());
        var answer = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
        return answer["errors"]?.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>())
            ?? new Dictionary<string, string> { [string.Empty] = answer["error"]!.GetValue<string>() };
    }

    [Fact]
    public async Task TheCatalogOffersThePageTasksAndThePkiReadsAndTheClientsMenusStayAsTheyWere()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        var catalog = await server.ToolAsync(key, "oadm_tasks_catalog", new JsonObject());
        var tasks = catalog["tasks"]!.AsArray().ToDictionary(t => t!["task"]!.GetValue<string>(), t => t!.AsObject());
        foreach (var id in new[]
                 {
                     EdgeStorageInfo.TaskPluginId, Rules::ActionRulesInfo.TaskPluginId, PkiTaskIds.Delete, PkiTaskIds.View, ConfigureIds.Configure,
                     ConfigureIds.Create, DeviceConfigIds.Export, DeviceConfigIds.Import,
                 })
        {
            Assert.True(tasks.ContainsKey(id), id);
            Assert.Equal("object", tasks[id]["input_schema"]!["type"]!.GetValue<string>());
        }

        Assert.Equal(["check", "repair", "retention", "format", "encryptionOff"], tasks[EdgeStorageInfo.TaskPluginId]["input_schema"]!["properties"]!["action"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));

        // The page tasks never become menu entries of the client.
        var menus = await server.Host.Tasks.ListTaskPluginsAsync(new Proto.ListTaskPluginsRequest());
        Assert.DoesNotContain(menus.Plugins, p => p.Id is EdgeStorageInfo.TaskPluginId or Rules::ActionRulesInfo.TaskPluginId);
    }

    [Fact]
    public async Task AFormatPreviewSaysThatRecordingsAreDeletedAndTheRunNeedsThePagesFormatRight()
    {
        await using var server = await StartAsync();
        var card = await AddAsync(server.Host, "10.31.0.1", 6101, DiskManagement);
        var none = await AddAsync(server.Host, "10.31.0.2", 6102);
        var key = await server.CreateKeyAsync();
        var tasksBefore = (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count;

        var format = await server.ToolAsync(key, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Filter(card, none), new JsonObject { ["action"] = "format" }));
        Assert.Equal(1, format["will_run"]!.GetValue<int>());
        Assert.Equal(card.Id.ToString(), Assert.Single(format["first"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal("The camera has no SD card management", Assert.Single(format["skipped"]!.AsArray())!["reason"]!.GetValue<string>());
        var warnings = format["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains(StoragePayloadRules.DeletesRecordings, warnings);
        Assert.Contains(StoragePayloadRules.StopsRecording, warnings);
        Assert.Equal(tasksBefore, (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count); // nothing written

        // The page's rules at their fields; encrypt needs a passphrase, so it stays on the page.
        var days = Errors(await server.CallToolAsync(key, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Filter(card), new JsonObject { ["action"] = "retention" })));
        Assert.Equal(StoragePayloadRules.RetentionRange, days["input.retentionDays"]);
        var encrypt = Errors(await server.CallToolAsync(key, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Filter(card), new JsonObject { ["action"] = "encryptionOn" })));
        Assert.Contains("input.action", encrypt.Keys);

        // An Operator key may check the cards (the page's "Change storage settings") but not format them.
        var operatorKey = await server.CreateKeyAsync(name: "op", roleId: BuiltInRoles.OperatorId.ToString());
        var check = await server.ToolAsync(operatorKey, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Filter(card), new JsonObject { ["action"] = "check" }));
        Assert.Equal(1, check["will_run"]!.GetValue<int>());
        var refused = Errors(await server.CallToolAsync(operatorKey, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Filter(card), new JsonObject { ["action"] = "format" })));
        Assert.Contains("Format and encrypt storage", string.Join(" ", refused.Values), StringComparison.Ordinal);

        // The run takes the preview's handle and starts one task of the page task, owned by the key.
        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = format["handle"]!.GetValue<string>() });
        Assert.Equal(1, run["tasks"]!.GetValue<int>());
        var listed = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = run["batch"]!.GetValue<string>() });
        Assert.Equal(1, listed["total"]!.GetValue<int>());
        Assert.Equal(AccessKeyStore.AgentNameOf("agent"), listed["tasks"]![0]!["owner"]!.GetValue<string>());
    }

    /// <summary>
    /// The skill's workflow "configure devices": one parameter on many devices, previewed then run; a parameter that can cut
    /// the devices off warns and needs "Configure dangerous parameters"; a password is never taken.
    /// </summary>
    [Fact]
    public async Task ConfigureDevicesSetsAParameterOnManyDevicesAfterAPreviewAndWarnsAndRefusesLikeTheDialog()
    {
        await using var server = await StartAsync();
        var param = new DeviceApi(ConfigureDevicesTask.ParamApi, "1.0");
        var one = await AddAsync(server.Host, "10.34.0.1", 6401, param);
        var two = await AddAsync(server.Host, "10.34.0.2", 6402, param);
        var down = await server.Host.Get<DeviceRepository>().AddAsync(
            new Device { Serial = FakeSerials.Make(6403), Address = "10.34.0.3", Scheme = DeviceScheme.Http, Status = DeviceStatus.Unreachable, Model = "M3085-V", Apis = [param] },
            CancellationToken.None);
        var operatorKey = await server.CreateKeyAsync(name: "op", roleId: BuiltInRoles.OperatorId.ToString());
        static JsonObject Set(string name, string value) => new() { ["settings"] = new JsonArray(new JsonObject { ["name"] = name, ["value"] = value }) };

        var preview = await server.ToolAsync(operatorKey, "oadm_task_preview", Preview(ConfigureIds.Configure, Filter(one, two, down), Set("ImageSource.I0.Sensor.CaptureFrequency", "50Hz")));
        Assert.Equal(2, preview["will_run"]!.GetValue<int>());
        Assert.Single(preview["skipped"]!.AsArray()); // the unreachable one, by its status
        Assert.Equal("Configure 1 parameter", preview["task_name"]!.GetValue<string>());
        Assert.NotEmpty(preview["warnings"]!.AsArray()); // each device checks the value against its own parameters first

        // A parameter that can cut the devices off: the dialog's warning, and the Operator lacks the right to set it.
        var adminKey = await server.CreateKeyAsync();
        var dns = await server.ToolAsync(adminKey, "oadm_task_preview", Preview(ConfigureIds.Configure, Filter(one, two), Set("Network.DNSServer1", "10.34.0.250")));
        Assert.Contains(dns["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("may become unreachable", StringComparison.Ordinal));
        var dangerous = Errors(await server.CallToolAsync(operatorKey, "oadm_task_preview", Preview(ConfigureIds.Configure, Filter(one), Set("Network.DNSServer1", "10.34.0.250"))));
        Assert.Contains("Configure dangerous parameters", string.Join(" ", dangerous.Values), StringComparison.Ordinal);

        // A password is never taken from an agent.
        var secret = Errors(await server.CallToolAsync(adminKey, "oadm_task_preview", Preview(ConfigureIds.Configure, Filter(one), Set("Network.Wireless.Passphrase", "x"))));
        Assert.Contains(secret, e => e.Key.StartsWith("input.settings", StringComparison.Ordinal) && e.Value.Contains("Set the password in OADM", StringComparison.Ordinal));

        // Connection settings of an import need "Import connection settings".
        var connection = Errors(await server.CallToolAsync(operatorKey, "oadm_task_preview", Preview(DeviceConfigIds.Import, Filter(one), new JsonObject
        {
            ["fileId"] = "0123456789abcdef0123456789abcdef",
            ["fileName"] = "lobby.json",
            ["sections"] = new JsonArray("connection"),
        })));
        Assert.Contains("Import connection settings", string.Join(" ", connection.Values), StringComparison.Ordinal);

        // The run of the first preview: one task per device that would run, owned by the key.
        var run = await server.ToolAsync(operatorKey, "oadm_task_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(2, run["tasks"]!.GetValue<int>());
        var listed = await server.ToolAsync(operatorKey, "oadm_tasks_list", new JsonObject { ["batch"] = run["batch"]!.GetValue<string>() });
        Assert.Equal(2, listed["total"]!.GetValue<int>());
        Assert.All(listed["tasks"]!.AsArray(), t => Assert.Equal(AccessKeyStore.AgentNameOf("op"), t!["owner"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ActionRulesPlanFromTheEditorsLastReadAndRefuseAPassword()
    {
        await using var server = await StartAsync();
        var device = await AddAsync(server.Host, "10.32.0.1", 6201);
        var key = await server.CreateKeyAsync();

        // Nothing was read in the editor yet: the device is skipped with the reason, no handle.
        var delete = await server.ToolAsync(key, "oadm_task_preview", Preview(Rules::ActionRulesInfo.TaskPluginId, Filter(device), new JsonObject { ["action"] = "delete", ["ruleName"] = "Siren" }));
        Assert.Equal(0, delete["will_run"]!.GetValue<int>());
        Assert.Equal(Rules::ActionRulesPlugin.NotReadText, Assert.Single(delete["skipped"]!.AsArray())!["reason"]!.GetValue<string>());

        var missing = Errors(await server.CallToolAsync(key, "oadm_task_preview", Preview(Rules::ActionRulesInfo.TaskPluginId, Filter(device), new JsonObject { ["action"] = "delete" })));
        Assert.Contains("input.ruleName", missing.Keys);
    }

    [Fact]
    public async Task DeleteCertificatesNamesWhatIsDeletedAndSkipsADeviceWithoutAChoice()
    {
        await using var server = await StartAsync();
        var one = await AddAsync(server.Host, "10.33.0.1", 6301, new DeviceApi("cert", "1.0"));
        var two = await AddAsync(server.Host, "10.33.0.2", 6302, new DeviceApi("cert", "1.0"));
        var key = await server.CreateKeyAsync();
        var input = new JsonObject
        {
            ["devices"] = new JsonObject { [one.Id.ToString()] = new JsonArray(new JsonObject { ["alias"] = "Old HTTPS", ["ca"] = false }) },
        };

        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview(PkiTaskIds.Delete, Filter(one, two), input));
        var skipped = preview["skipped"]!.AsArray().Select(s => s!["reason"]!.GetValue<string>()).ToList();
        Assert.Contains("No certificate of this device was chosen", skipped);
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains(DeleteTexts.CannotRestore, StringComparison.Ordinal));

        var none = Errors(await server.CallToolAsync(key, "oadm_task_preview", Preview(PkiTaskIds.Delete, Filter(one), new JsonObject { ["devices"] = new JsonObject() })));
        Assert.Equal(DeleteTexts.NoneChosen, none["input.devices"]);
    }
}
