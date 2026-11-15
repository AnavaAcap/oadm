using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The logins an agent's user types through the secret channel (docs/spec/mcp.md "Secrets"): Log in on managed devices, the
/// first password of devices in factory default (the devices' passphrase policies checked when it is entered), the add run's
/// typed login and first password, and the credential list (Administrator only). The password goes from the link (or the
/// assistant's card) to the service once and is in no answer, audit entry, log line, task or file of the data folder.
/// </summary>
public sealed class McpLoginToolTests
{
    /// <summary>The tools of this part (<see cref="McpHostActionTests.ToolNames"/> lists them).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_credentials_add_preview", "oadm_credentials_add_run", "oadm_credentials_remove_preview", "oadm_credentials_remove_run",
        "oadm_devices_first_password_preview", "oadm_devices_first_password_run", "oadm_devices_login_preview", "oadm_devices_login_run",
    ];

    /// <summary>The gRPC methods these tools call besides those of the other parts.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.AddDevicesService/RetryAuth", "/oadm.v1.DeviceService/GetPassphrasePolicies", "/oadm.v1.DeviceService/LogIn",
        "/oadm.v1.DeviceService/SetFirstPassword", "/oadm.v1.SettingsService/AddCredential", "/oadm.v1.SettingsService/RemoveCredential",
    ];

    private const string Canary = "canary-Login-Pass-7Qx!e";
    private const string FirstCanary = "canary-First-Pass-9Zp!q2";

    private static async Task<McpTestServer> StartAsync(FakeAxisNetwork network) =>
        await McpTestServer.StartAsync(network, configureServices: services =>
        {
            services.RemoveAll<IServerNetworkInterfaces>();
            services.AddSingleton<IServerNetworkInterfaces>(new OwnCard());
        });

    /// <summary>The server's own network of these tests: 10.9.0.0/24.</summary>
    private sealed class OwnCard : IServerNetworkInterfaces
    {
        public IReadOnlyList<ServerNetworkInterface> List()
        {
            var ip = IPAddress.Parse("10.9.0.254");
            return [new ServerNetworkInterface("eth0", "eth0", "eth0", [ip], true, false) { PrefixLengths = new Dictionary<IPAddress, int> { [ip] = 24 } }];
        }
    }

    private static async Task<HttpStatusCode> PutAsync(McpTestServer server, string key, JsonObject preview, string slot, string value, StringBuilder answers)
    {
        var url = preview["secret_links"]![slot]!["url"]!.GetValue<string>();
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await client.SendAsync(request);
        answers.Append(await response.Content.ReadAsStringAsync());
        return response.StatusCode;
    }

    private static Proto.AssistantPreviewCard Card(McpTestServer server, string preview, JsonObject answer, JsonObject arguments)
    {
        var registry = server.Host.Get<ToolRegistry>();
        Assert.True(registry.TryGet(preview, out var tool));
        registry.TryGet(AssistantTools.RunOf(preview), out var run);
        return AssistantCards.Build(tool, run, answer["handle"]!.GetValue<string>(), answer, System.Text.Json.JsonSerializer.SerializeToElement(arguments));
    }

    [Fact]
    public async Task ATypedLoginGoesThroughItsLinkAndIsStoredOnlyWhereTheDeviceAcceptsIt()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), Canary);
        network.Add("10.9.0.2", FakeSerials.Make(2), "another-one");
        await using var server = await StartAsync(network);
        var first = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.9.0.1", 1, password: "old", status: DeviceStatus.CredentialsRequired);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.9.0.2", 2, password: "old", status: DeviceStatus.CredentialsRequired);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var answers = new StringBuilder();

        var arguments = new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.9.0." }, ["user_name"] = "root" };
        var preview = await server.ToolAsync(key, "oadm_devices_login_preview", arguments);
        answers.Append(preview.ToJsonString());
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        var card = Card(server, "oadm_devices_login_preview", preview, arguments);
        Assert.Contains(card.Details, d => d.Contains("root", StringComparison.Ordinal));
        Assert.True(card.Destructive);

        var early = await server.CallToolAsync(key, "oadm_devices_login_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the password.", early.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await PutAsync(server, key, preview, "password", Canary, answers));

        var run = await server.ToolAsync(key, "oadm_devices_login_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        Assert.Equal((1, 1), (run["ok"]!.GetValue<int>(), run["failed"]!.GetValue<int>()));
        var stored = await server.Host.Get<CredentialStore>().GetAsync(first.Id, CancellationToken.None);
        Assert.Equal(("root", Canary), (stored!.UserName, stored.Password));
        Assert.Contains((await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries,
            e => e.Action == AuditActions.SecretSet && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot") && e.Target == "password");
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }

    [Fact]
    public async Task TheFirstPasswordIsCheckedAgainstTheDevicesPolicyWhenEnteredAndSetOnlyOnFactoryDefaultDevices()
    {
        var network = new FakeAxisNetwork();
        var fresh = network.Add("10.9.0.3", FakeSerials.Make(3), password: null);
        fresh.PassphrasePolicy = "length";
        fresh.MinimumPasswordLength = 15;
        network.Add("10.9.0.4", FakeSerials.Make(4), "has-one");
        await using var server = await StartAsync(network);
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.9.0.3", 3, password: null, status: DeviceStatus.PasswordNotSet);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.9.0.4", 4, password: "has-one");
        var key = await server.CreateKeyAsync();
        var answers = new StringBuilder();

        var preview = await server.ToolAsync(key, "oadm_devices_first_password_preview", new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.9.0." } });
        answers.Append(preview.ToJsonString());
        Assert.Equal(1, preview["devices"]!.GetValue<int>()); // only the device in factory default
        Assert.Contains("15", preview["rules"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, await PutAsync(server, key, preview, "password", "Short-1!", answers)); // the device's rule, at once
        Assert.Contains("at least 15 characters", answers.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await PutAsync(server, key, preview, "password", FirstCanary, answers)); // the link stayed

        var run = await server.ToolAsync(key, "oadm_devices_first_password_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        Assert.Equal(1, run["ok"]!.GetValue<int>());
        Assert.Equal(FirstCanary, fresh.Password);
        Assert.Equal(FirstCanary, (await server.Host.Get<CredentialStore>().GetAsync(device.Id, CancellationToken.None))!.Password);
        await SecretScan.AssertNowhereAsync(server.Host, FirstCanary, answers.ToString());

        var none = await server.ToolAsync(key, "oadm_devices_first_password_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) } });
        Assert.Null(none["handle"]); // it has a password now: nothing to do
    }

    [Fact]
    public async Task AnAddRunTriesTheTypedLoginWhereTheListFailsAndGivesFactoryDefaultDevicesTheFirstPassword()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), Canary);
        network.Add("10.9.0.2", FakeSerials.Make(2), "another-one");
        network.Add("10.9.0.3", FakeSerials.Make(3), password: null).PassphrasePolicy = "none";
        await using var server = await StartAsync(network);
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "list-password" });
        var key = await server.CreateKeyAsync();
        var answers = new StringBuilder();

        var arguments = new JsonObject { ["range"] = new JsonObject { ["from"] = "10.9.0.1", ["to"] = "10.9.0.3" }, ["user_name"] = "root", ["first_password"] = true };
        var preview = await server.ToolAsync(key, "oadm_devices_add_preview", arguments);
        answers.Append(preview.ToJsonString());
        Assert.Equal(3, preview["will_try"]!.GetValue<int>());
        Assert.Equal(["password", "first_password"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.NotNull(preview["first_password_rules"]);
        Assert.Contains(Card(server, "oadm_devices_add_preview", preview, arguments).Details, d => d.Contains("root", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, await PutAsync(server, key, preview, "password", Canary, answers));
        var early = await server.CallToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Contains("Enter the first password.", early.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await PutAsync(server, key, preview, "first_password", FirstCanary, answers));

        var run = await server.ToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        Assert.Equal((2, 1), (run["added"]!.GetValue<int>(), run["not_added"]!.GetValue<int>())); // .2 rejects both logins
        Assert.Equal(FirstCanary, network["10.9.0.3"].Password);
        var devices = (await server.Host.Devices.ListAsync(new Proto.Empty())).Devices.ToDictionary(d => d.Address);
        Assert.Equal(["10.9.0.1", "10.9.0.3"], devices.Keys.Order(StringComparer.Ordinal));
        var stored = await server.Host.Get<CredentialStore>().GetAsync(Guid.Parse(devices["10.9.0.1"].Id), CancellationToken.None);
        Assert.Equal(Canary, stored!.Password);
        Assert.DoesNotContain((await server.Host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries, e => e.UserName != "root"); // never saved to the list
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
        await SecretScan.AssertNowhereAsync(server.Host, FirstCanary, answers.ToString());
    }

    [Fact]
    public async Task TheCredentialListTakesAnEntryThroughTheLinkOnlyFromAnAdministratorAndRemovesItByName()
    {
        await using var server = await StartAsync(new FakeAxisNetwork());
        await GroupCallers.DeviceAsync(server.Host, 41, "Lobby");
        var key = await server.CreateKeyAsync(name: "Admin-bot");
        var answers = new StringBuilder();

        var preview = await server.ToolAsync(key, "oadm_credentials_add_preview", new JsonObject { ["user_name"] = "installer" });
        answers.Append(preview.ToJsonString());
        Assert.Equal("1 credential", Card(server, "oadm_credentials_add_preview", preview, []).CountText);
        Assert.Empty((await server.Host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries); // nothing written
        Assert.Equal(HttpStatusCode.OK, await PutAsync(server, key, preview, "password", Canary, answers));
        var run = await server.ToolAsync(key, "oadm_credentials_add_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        var entry = Assert.Single((await server.Host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);
        Assert.Equal("installer", entry.UserName);
        Assert.Contains((await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries,
            e => e.Action == AuditActions.CredentialAdded && e.UserName == AccessKeyStore.AgentNameOf("Admin-bot"));
        var twice = await server.CallToolAsync(key, "oadm_credentials_add_preview", new JsonObject { ["user_name"] = "installer" });
        Assert.Contains(LoginTools.InList, twice.ToJsonString(), StringComparison.Ordinal);
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());

        // Only an Administrator with all devices sees the list; a group user or an operator sees none of its tools.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupNames = await server.ToolNamesAsync(await server.CreateKeyAsync(group.Invoker));
        Assert.DoesNotContain(groupNames, n => n.StartsWith("oadm_credentials_", StringComparison.Ordinal));
        Assert.Contains("oadm_devices_login_preview", groupNames); // its own devices' logins it may type

        var remove = await server.ToolAsync(key, "oadm_credentials_remove_preview", new JsonObject { ["user_name"] = "installer" });
        Assert.True(Card(server, "oadm_credentials_remove_preview", remove, []).Destructive);
        await server.ToolAsync(key, "oadm_credentials_remove_run", new JsonObject { ["handle"] = remove["handle"]!.GetValue<string>() });
        Assert.Empty((await server.Host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);
        var unknown = await server.CallToolAsync(key, "oadm_credentials_remove_preview", new JsonObject { ["user_name"] = "installer" });
        Assert.Contains(LoginTools.NotInList, unknown.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGroupUserLogsInOnlyToItsOwnDevices()
    {
        var network = new FakeAxisNetwork();
        await using var server = await StartAsync(network);
        var inside = await GroupCallers.DeviceAsync(server.Host, 51, "Lobby");
        var outside = await GroupCallers.DeviceAsync(server.Host, 52);
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var key = await server.CreateKeyAsync(group.Invoker);

        var preview = await server.ToolAsync(key, "oadm_devices_login_preview", new JsonObject { ["filter"] = new JsonObject(), ["user_name"] = "root" });
        Assert.Equal(1, preview["devices"]!.GetValue<int>());
        Assert.DoesNotContain(outside.Id.ToString(), preview.ToJsonString(), StringComparison.Ordinal);
        var named = await server.CallToolAsync(key, "oadm_devices_login_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(outside.Id.ToString()) }, ["user_name"] = "root" });
        Assert.True(named["isError"]!.GetValue<bool>());
        Assert.NotEqual(inside.Id, outside.Id);
    }
}
