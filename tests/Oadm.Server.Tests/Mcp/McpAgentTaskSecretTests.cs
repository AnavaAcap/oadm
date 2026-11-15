using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Plugins.Acap;
using Oadm.Plugins.DeviceConfig;
using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Plugins.DeviceConfig.Tools;
using Oadm.Plugins.EdgeStorage;
using Oadm.Sdk;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The tasks whose secrets an agent's user enters through the secret channel (docs/spec/mcp.md "Secrets"): Configure devices'
/// password parameters, Edge storage's encrypt passphrase, the password settings and the license key of Applications. Per task:
/// the preview names the slot with its label, a value the plugin's rules refuse is refused at the link (which stays), the run
/// takes the entered value, the rights and the device group apply, and a canary value is nowhere else.
/// </summary>
public sealed class McpAgentTaskSecretTests
{
    private static readonly DeviceApi DiskManagement = new("disk-management", "1.0");
    private static readonly DeviceApi Param = new(ConfigureDevicesTask.ParamApi, "1.0");
    private static readonly DeviceApi Application = new("application", "1.0");

    private static async Task<McpTestServer> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var storage = new EdgeStoragePlugin();
        registry.RegisterCorePlugin(storage, new PluginOrigin(storage.Id, "1.0.0", null) { DisplayName = storage.DisplayName, MinSdkVersion = SdkInfo.Version });
        var deviceConfig = new PluginOrigin(DeviceConfigIds.PackageId, "1.0.0", null) { DisplayName = "Device configuration", AlwaysOn = true, MinSdkVersion = SdkInfo.Version };
        registry.RegisterCorePlugin(new DeviceConfigToolsPlugin(), deviceConfig);
        Assert.True(registry.RegisterTaskPlugin(new ConfigureDevicesTask(), deviceConfig));
        Assert.True(registry.RegisterTaskPlugin(new AcapTaskPlugin(), new PluginOrigin("oadm.acap", "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
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

    private static JsonObject Preview(string task, JsonObject filter, JsonObject input) => new() { ["task"] = task, ["filter"] = filter, ["input"] = input.DeepClone() };

    private static JsonObject Search(string text) => new() { ["search"] = text };

    private static async Task<(HttpStatusCode Status, string Body)> PutAsync(McpTestServer server, string key, string url, string value)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The secret path of one preview: the slot <paramref name="slot"/> with its label, <paramref name="refused"/> refused at the
    /// link with <paramref name="refusal"/>, then <paramref name="canary"/> taken and the run started; the answers collected.
    /// </summary>
    private static async Task<string> SecretRunAsync(McpTestServer server, string key, JsonObject preview, string slot, string label, string? refused, string? refusal, string canary)
    {
        var answers = new StringBuilder(preview.ToJsonString());
        Assert.Equal(slot, Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        var link = preview["secret_links"]![slot]!;
        Assert.Contains(char.ToUpperInvariant(label[0]) + label[1..] + ": ", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var handle = preview["handle"]!.GetValue<string>();
        var early = await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the " + label + ".", early.ToJsonString(), StringComparison.Ordinal);

        var url = link["url"]!.GetValue<string>();
        if (refused is not null)
        {
            var bad = await PutAsync(server, key, url, refused);
            Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
            Assert.Contains(refusal!, bad.Body, StringComparison.Ordinal);
        }

        var set = await PutAsync(server, key, url, canary);
        Assert.Equal(HttpStatusCode.OK, set.Status);
        answers.Append(set.Body);
        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.Equal(1, run["tasks"]!.GetValue<int>());
        answers.Append((await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = run["batch"]!.GetValue<string>() })).ToJsonString());
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == label);
        return answers.ToString();
    }

    [Fact]
    public async Task ConfigureDevicesSetsAPasswordParameterTheUserEnters()
    {
        const string canary = "canary-config-secret-71";
        await using var server = await StartAsync();
        var one = await AddAsync(server.Host, "10.41.0.1", 7101, Param);
        await AddAsync(server.Host, "10.41.0.2", 7102, Param);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var input = new JsonObject { ["settings"] = new JsonArray(new JsonObject { ["name"] = "RemoteService.ProxyPassword", ["value"] = string.Empty }) };

        var all = await server.ToolAsync(key, "oadm_task_preview", Preview(ConfigureIds.Configure, Search("10.41.0."), input));
        Assert.Equal(2, all["will_run"]!.GetValue<int>());
        Assert.Equal(one.Id.ToString(), all["first"]![0]!["id"]!.GetValue<string>());

        // A Viewer key has no task tool.
        var viewer = await server.CreateKeyAsync(name: "viewer", roleId: BuiltInRoles.ViewerId.ToString());
        Assert.True((await server.CallToolAsync(viewer, "oadm_task_preview", Preview(ConfigureIds.Configure, Search("10.41.0."), input)))["isError"]!.GetValue<bool>());

        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview(ConfigureIds.Configure, Search("10.41.0.1"), input));
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>() == ConfigureDevicesTask.SecretsNote(["RemoteService.ProxyPassword"]));
        var answers = await SecretRunAsync(server, key, preview, "setting_0", "password for RemoteService.ProxyPassword",
            "a\u0001b", "Line breaks and control characters are not allowed.", canary);
        await SecretScan.AssertNowhereAsync(server.Host, canary, answers);
    }

    [Fact]
    public async Task EdgeStorageEncryptsWithAPassphraseTheUserEntersAndNeedsTheFormatRight()
    {
        const string canary = "canary-edge-passphrase-72";
        await using var server = await StartAsync();
        await AddAsync(server.Host, "10.42.0.1", 7201, DiskManagement);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var input = new JsonObject { ["action"] = "encryptionOn" };

        var operatorKey = await server.CreateKeyAsync(name: "op", roleId: BuiltInRoles.OperatorId.ToString());
        var refused = await server.CallToolAsync(operatorKey, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Search("10.42.0.1"), input));
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Format and encrypt storage", refused.ToJsonString(), StringComparison.Ordinal);

        var sent = await server.CallToolAsync(key, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Search("10.42.0.1"), new JsonObject { ["action"] = "encryptionOn", ["passphrase"] = "typed" }));
        Assert.True(sent["isError"]!.GetValue<bool>()); // no input of the agent

        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview(EdgeStorageInfo.TaskPluginId, Search("10.42.0.1"), input));
        var warnings = preview["warnings"]!.AsArray().Select(w => w!.GetValue<string>()).ToList();
        Assert.Contains(StoragePayloadRules.PassphraseNote, warnings);
        Assert.Contains(StoragePayloadRules.DeletesRecordings, warnings);
        var answers = await SecretRunAsync(server, key, preview, "passphrase", "passphrase", "a\u0001b", "The passphrase contains characters the camera does not accept.", canary);
        await SecretScan.AssertNowhereAsync(server.Host, canary, answers);
    }

    [Fact]
    public async Task ApplicationsSetAPasswordSettingTheUserEnters()
    {
        const string canary = "canary-acap-password-73";
        await using var server = await StartAsync();
        var one = await AddAsync(server.Host, "10.43.0.1", 7301, Application);
        await AddAsync(server.Host, "10.43.0.2", 7302, Application);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");

        // A group key plans only its own device.
        var tag = await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = "Lobby" });
        await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { one.Id.ToString() }, Add = { "Lobby" } });
        var group = await GroupCallers.GroupAsync(server.Host, "Lobby group", new Proto.DeviceGroupRules { TagIds = { tag.Id } });
        var grouped = await server.CreateKeyAsync(name: "grouped", roleId: BuiltInRoles.OperatorId.ToString(), groupId: group.ToString());
        var mine = await server.ToolAsync(grouped, "oadm_task_preview", Preview("oadm.acap", Search("10.43.0."), new JsonObject
        {
            ["action"] = "settings",
            ["application"] = "vmd",
            ["passwords"] = new JsonArray(new JsonObject { ["name"] = "Mqtt.Password" }),
        }));
        Assert.Equal(1, mine["will_run"]!.GetValue<int>());
        Assert.Equal(one.Id.ToString(), mine["first"]![0]!["id"]!.GetValue<string>());

        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview("oadm.acap", Search("10.43.0.1"), new JsonObject
        {
            ["action"] = "settings",
            ["application"] = "vmd",
            ["passwords"] = new JsonArray(new JsonObject { ["name"] = "Mqtt.Password" }),
        }));
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>() == AcapTaskPlugin.PasswordsNote(["Mqtt.Password"]));
        var answers = await SecretRunAsync(server, key, preview, "password_0", "password for Mqtt.Password", "a\u0001b", "Mqtt.Password", canary);
        await SecretScan.AssertNowhereAsync(server.Host, canary, answers);
    }

    [Fact]
    public async Task ApplicationsInstallALicenseKeyTheUserEntersOnOneDevice()
    {
        const string signature = "Y2FuYXJ5LWFjYXAta2V5LTc0canary";
        var licenseKey = $"""
            <LicenseKey>
                <FormatVersion>1</FormatVersion>
                <ApplicationID>415090</ApplicationID>
                <MinimumMajorVersion>-1</MinimumMajorVersion>
                <MinimumMinorVersion>-1</MinimumMinorVersion>
                <MaximumMajorVersion>-1</MaximumMajorVersion>
                <MaximumMinorVersion>-1</MaximumMinorVersion>
                <ExpirationDate>2099-09-18</ExpirationDate>
                <DeviceID>B8A44F631339</DeviceID>
                <SignatureKeyID>1</SignatureKeyID>
                <Signature>{signature}</Signature>
            </LicenseKey>
            """;
        await using var server = await StartAsync();
        await AddAsync(server.Host, "10.44.0.1", 7401, Application);
        await AddAsync(server.Host, "10.44.0.2", 7402, Application);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var input = new JsonObject { ["action"] = "installLicense", ["application"] = "ax_msf" };

        // A key file fits one device: a preview of two runs none.
        var two = await server.ToolAsync(key, "oadm_task_preview", Preview("oadm.acap", Search("10.44.0."), input.DeepClone().AsObject()));
        Assert.Equal(0, two["will_run"]!.GetValue<int>());
        Assert.Equal(AcapTaskPlugin.OneDeviceForAKey, two["skipped"]![0]!["reason"]!.GetValue<string>());

        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview("oadm.acap", Search("10.44.0.1"), input));
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>() == AcapTaskPlugin.LicenseKeyNote);
        var answers = await SecretRunAsync(server, key, preview, AcapTaskPlugin.LicenseKeySlot, "license key", "not a key", "This is not a license key file.", licenseKey);
        await SecretScan.AssertNowhereAsync(server.Host, signature, answers);
    }
}
