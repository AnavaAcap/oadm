using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Sdk.Plugins;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Users, roles, device groups and access keys for AI agents (docs/spec/mcp.md "Access", decision 2026-11-15): only an
/// Administrator's agent sees them; every change is a preview that writes nothing and a run with the Permissions page's checks,
/// refusals and audit; never the last administrator, never the caller's own role, state or account, never the agent's own key;
/// passwords only through the secret channel and a new key only through its one-time link or card, never in an answer, a log
/// line or the audit log.
/// </summary>
public sealed class McpAccessToolTests
{
    /// <summary>The tools of this area (<see cref="McpToolTests"/> lists them with the others).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_access_keys_create_preview", "oadm_access_keys_create_run", "oadm_access_keys_delete_preview", "oadm_access_keys_delete_run",
        "oadm_access_keys_list", "oadm_device_groups_delete_preview", "oadm_device_groups_delete_run", "oadm_device_groups_list",
        "oadm_device_groups_save_preview", "oadm_device_groups_save_run", "oadm_roles_delete_preview", "oadm_roles_delete_run", "oadm_roles_list",
        "oadm_roles_permissions", "oadm_roles_save_preview", "oadm_roles_save_run", "oadm_users_add_preview", "oadm_users_add_run",
        "oadm_users_delete_preview", "oadm_users_delete_run", "oadm_users_list", "oadm_users_password_preview", "oadm_users_password_run",
        "oadm_users_update_preview", "oadm_users_update_run",
    ];

    /// <summary>The gRPC methods these tools call besides those of the other areas.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.AccessKeyService/Create", "/oadm.v1.AccessKeyService/Delete", "/oadm.v1.AccessKeyService/List",
        "/oadm.v1.DeviceGroupService/Delete", "/oadm.v1.DeviceGroupService/List", "/oadm.v1.DeviceGroupService/Preview", "/oadm.v1.DeviceGroupService/Save",
        "/oadm.v1.RoleService/DeleteRole", "/oadm.v1.RoleService/GetMatrix", "/oadm.v1.RoleService/SaveRoles",
        "/oadm.v1.UserService/Add", "/oadm.v1.UserService/Delete", "/oadm.v1.UserService/List", "/oadm.v1.UserService/Update",
    ];

    /// <summary>The run tools of this area.</summary>
    public static string[] RunTools => [.. ToolNames.Where(n => n.EndsWith("_run", StringComparison.Ordinal))];

    /// <summary>The runs that only add (<c>destructiveHint</c> false).</summary>
    public static readonly string[] AddingRuns = ["oadm_access_keys_create_run", "oadm_users_add_run"];

    /// <summary>One call of every tool of this area for the scan of <see cref="McpSecretTests"/>.</summary>
    public static Dictionary<string, JsonObject> SecretScanCalls()
    {
        var calls = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["oadm_users_list"] = [],
            ["oadm_users_add_preview"] = new() { ["user_name"] = "scan-user", ["role"] = "Viewer", ["devices"] = "all" },
            ["oadm_users_update_preview"] = new() { ["user"] = "admin", ["enabled"] = false },
            ["oadm_users_delete_preview"] = new() { ["user"] = "admin" },
            ["oadm_users_password_preview"] = new() { ["user"] = "admin" },
            ["oadm_roles_list"] = [],
            ["oadm_roles_permissions"] = new() { ["role"] = "Operator" },
            ["oadm_roles_save_preview"] = new() { ["name"] = "Scan", ["grant"] = new JsonArray("devices.refresh") },
            ["oadm_roles_delete_preview"] = new() { ["role"] = "Operator" },
            ["oadm_device_groups_list"] = [],
            ["oadm_device_groups_save_preview"] = new() { ["name"] = "Scan", ["this_server"] = true },
            ["oadm_device_groups_delete_preview"] = new() { ["group"] = "none" },
            ["oadm_access_keys_list"] = [],
            ["oadm_access_keys_create_preview"] = new() { ["name"] = "scan", ["role"] = "Viewer", ["devices"] = "all" },
            ["oadm_access_keys_delete_preview"] = new() { ["key"] = "none" },
        };
        foreach (var run in RunTools)
        {
            calls[run] = new JsonObject { ["handle"] = "ended" };
        }

        return calls;
    }

    private const string Canary = "canary-agent-password-7c1d";

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    /// <summary>The warnings of a preview's plan, one a line.</summary>
    private static string Warnings(JsonObject answer) => string.Join("\n", answer["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));

    private static async Task<IReadOnlyList<Proto.AuditEntry>> AuditAsync(McpTestServer server) =>
        (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;

    private static Proto.AssistantPreviewCard Card(McpTestServer server, string preview, JsonObject answer)
    {
        var registry = server.Host.Get<ToolRegistry>();
        Assert.True(registry.TryGet(preview, out var tool));
        registry.TryGet(AssistantTools.RunOf(preview), out var run);
        var card = AssistantCards.Build(tool, run, answer["handle"]?.GetValue<string>() ?? "h", answer, JsonSerializer.SerializeToElement(new JsonObject()));
        Assert.NotEmpty(card.Details);
        Assert.False(string.IsNullOrEmpty(card.CountText));
        return card;
    }

    private static async Task<HttpResponseMessage> SendAsync(McpTestServer server, HttpMethod method, string key, string url, byte[]? body = null)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(method, new Uri(url).PathAndQuery);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>Sets the secret of a preview through its one-time link, as the user's shell does.</summary>
    private static async Task SetSecretAsync(McpTestServer server, string key, JsonObject preview, string value)
    {
        var url = preview["secret_links"]!["password"]!["url"]!.GetValue<string>();
        using var set = await SendAsync(server, HttpMethod.Put, key, url, Encoding.UTF8.GetBytes(value + "\n"));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    private static async Task<Proto.UserInfo?> UserAsync(McpTestServer server, string name) =>
        (await server.Host.Users.ListAsync(new Proto.Empty())).Users.FirstOrDefault(u => u.UserName == name);

    /// <summary>The administrator "admin" as a signed-in caller of the tools (the assistant's channel): its own session.</summary>
    private static async Task<ToolCaller> AdminSessionAsync(McpTestServer server)
    {
        var admin = (await server.Host.Get<UserStore>().FindByNameAsync("admin", CancellationToken.None))!;
        var hash = AuthTokenStore.HashOf(server.Host.AdminToken);
        var caller = new AuthenticatedCaller(admin.Id, "admin", BuiltInRoles.AdministratorId, PermissionSet.All, "127.0.0.1", "testpc", hash);
        return new ToolCaller(caller, ToolHandles.BindingKeyOf(admin.Id, hash));
    }

    [Fact]
    public async Task OnlyAnAdministratorsAgentListsTheAccessToolsAndAnyOtherCallerIsRefused()
    {
        await using var server = await McpTestServer.StartAsync();
        var admin = await server.CreateKeyAsync();
        var names = await server.ToolNamesAsync(admin);
        Assert.All(ToolNames, name => Assert.Contains(name, names));

        // A role with every right a role can be given (still not Administrator) and a user of a device group: none of them.
        var everything = await GroupCallers.EverythingRoleAsync(server.Host);
        var operatorKey = await server.CreateKeyAsync(name: "everything", roleId: everything.ToString());
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker, name: "tech-key");
        foreach (var key in new[] { operatorKey, groupKey })
        {
            var listed = await server.ToolNamesAsync(key);
            Assert.All(ToolNames, name => Assert.DoesNotContain(name, listed));
            var refused = await server.CallToolAsync(key, "oadm_users_add_preview", new JsonObject { ["user_name"] = "mallory", ["role"] = "Administrator" });
            Assert.True(refused["isError"]!.GetValue<bool>());
            Assert.Contains(Oadm.Server.Auth.AuthInterceptor.AdminOnlyMessage, Text(refused), StringComparison.Ordinal);
        }

        Assert.Null(await UserAsync(server, "mallory"));
    }

    [Fact]
    public async Task AnAdministratorsAgentAddsAnAdministratorWithAPasswordOnlyFromTheSecretChannelAndThePlanSaysSo()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var answers = new StringBuilder();

        var taken = await server.CallToolAsync(key, "oadm_users_add_preview", new JsonObject { ["user_name"] = "ADMIN", ["role"] = "Viewer", ["devices"] = "all" });
        Assert.True(taken["isError"]!.GetValue<bool>());
        Assert.Contains(UserStore.NameTaken("ADMIN"), Text(taken), StringComparison.Ordinal);
        var noDevices = await server.CallToolAsync(key, "oadm_users_add_preview", new JsonObject { ["user_name"] = "bob", ["role"] = "Viewer" });
        Assert.Contains("\"field\":\"devices\"", Text(noDevices), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_users_add_preview", new JsonObject { ["user_name"] = "anna", ["role"] = "administrator" });
        answers.Append(preview.ToJsonString());
        Assert.True(preview["administrator"]!.GetValue<bool>());
        Assert.Equal("All devices", preview["devices"]!.GetValue<string>());
        Assert.Contains(AccessTexts.BecomesAdministrator("anna"), Warnings(preview), StringComparison.Ordinal);
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        Assert.Null(await UserAsync(server, "anna")); // nothing written
        var card = Card(server, "oadm_users_add_preview", preview);
        Assert.Equal("1 user", card.CountText);
        Assert.Contains(card.Warnings, w => w.Contains("becomes Administrator", StringComparison.Ordinal));

        var handle = preview["handle"]!.GetValue<string>();
        var early = await server.CallToolAsync(key, "oadm_users_add_run", new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the password.", Text(early), StringComparison.Ordinal);

        await SetSecretAsync(server, key, preview, Canary);
        var run = await server.ToolAsync(key, "oadm_users_add_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.Equal(("anna", "Administrator"), (run["user_name"]!.GetValue<string>(), run["role"]!.GetValue<string>()));
        var login = await server.Host.Auth.LoginAsync(new Proto.LoginRequest { UserName = "anna", Password = Canary });
        Assert.False(string.IsNullOrEmpty(login.Token));

        var again = await server.CallToolAsync(key, "oadm_users_add_run", new JsonObject { ["handle"] = handle });
        Assert.Contains(AreaListing.PreviewEnded, Text(again), StringComparison.Ordinal);

        var audit = await AuditAsync(server);
        Assert.Contains(audit, e => e.Action == AuditActions.UserAdded && e.Target == "anna" && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
        var list = await server.ToolAsync(key, "oadm_users_list", new JsonObject { ["search"] = "ann" });
        answers.Append(list.ToJsonString());
        Assert.Equal("anna", Assert.Single(list["users"]!.AsArray())!["user_name"]!.GetValue<string>());
        var logs = McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);
        Assert.DoesNotContain(Canary, answers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(audit, e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail).Contains(Canary, StringComparison.Ordinal));
        Assert.DoesNotContain(Canary, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLastAdministratorIsNeverDemotedDisabledOrDeletedAndTheCallerNeverLocksThemselvesOut()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();

        // An access key has no user: the guard of the last enabled administrator holds.
        foreach (var (tool, arguments) in new[]
        {
            ("oadm_users_update_preview", new JsonObject { ["user"] = "admin", ["role"] = "Operator" }),
            ("oadm_users_update_preview", new JsonObject { ["user"] = "admin", ["enabled"] = false }),
            ("oadm_users_delete_preview", new JsonObject { ["user"] = "admin" }),
        })
        {
            var refused = await server.CallToolAsync(key, tool, arguments);
            Assert.True(refused["isError"]!.GetValue<bool>(), tool);
            Assert.Contains(UserStore.LastAdministrator("admin"), Text(refused), StringComparison.Ordinal);
        }

        // The administrator's own session (the assistant): never the own role, state or account, as on the Permissions page.
        await server.Host.Users.AddAsync(new Proto.AddUserRequest { UserName = "second", Password = "pw", RoleId = BuiltInRoles.AdministratorId.ToString() });
        var registry = server.Host.Get<ToolRegistry>();
        var self = await AdminSessionAsync(server);
        var own = await registry.CallAsync(self, "oadm_users_update_preview", JsonSerializer.SerializeToElement(new { user = "admin", role = "Viewer" }), CancellationToken.None);
        Assert.Equal(UserStore.OwnRoleOrState, own.Error);
        var ownDelete = await registry.CallAsync(self, "oadm_users_delete_preview", JsonSerializer.SerializeToElement(new { user = "admin" }), CancellationToken.None);
        Assert.Equal(UserStore.OwnAccount, ownDelete.Error);

        // With a second administrator the agent may demote the first one, as an administrator could in OADM.
        var demote = await server.ToolAsync(key, "oadm_users_update_preview", new JsonObject { ["user"] = "second", ["role"] = "Operator" });
        Assert.Contains(AccessTexts.LosesAdministrator("second", "Operator"), Warnings(demote), StringComparison.Ordinal);
        Assert.Equal("Role: Administrator to Operator", Assert.Single(demote["changes"]!.AsArray())!.GetValue<string>());
    }

    [Fact]
    public async Task ADisabledUserOrANewPasswordEndsTheSessionsAndANewRoleAppliesToTheNextCall()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var anna = await server.Host.InvokerForUserAsync("anna", Oadm.Sdk.Plugins.UserRole.Operator);
        var annaAuth = new Proto.AuthService.AuthServiceClient(anna);
        Assert.Equal("Operator", (await annaAuth.MeAsync(new Proto.Empty())).RoleName);

        var role = await server.ToolAsync(key, "oadm_users_update_preview", new JsonObject { ["user"] = "anna", ["role"] = "Viewer" });
        Assert.False(role["ends_sessions"]!.GetValue<bool>());
        Assert.Equal("Operator", (await annaAuth.MeAsync(new Proto.Empty())).RoleName); // nothing written
        await server.ToolAsync(key, "oadm_users_update_run", new JsonObject { ["handle"] = role["handle"]!.GetValue<string>() });
        Assert.Equal("Viewer", (await annaAuth.MeAsync(new Proto.Empty())).RoleName); // the same session, the new role

        var password = await server.ToolAsync(key, "oadm_users_password_preview", new JsonObject { ["user"] = "anna" });
        Assert.Contains(AccessTexts.LoggedOutNewPassword("anna"), Warnings(password), StringComparison.Ordinal);
        await SetSecretAsync(server, key, password, Canary);
        await server.ToolAsync(key, "oadm_users_password_run", new JsonObject { ["handle"] = password["handle"]!.GetValue<string>() });
        var ended = await Assert.ThrowsAsync<RpcException>(async () => await annaAuth.MeAsync(new Proto.Empty()));
        Assert.Equal(StatusCode.Unauthenticated, ended.StatusCode);
        var login = await server.Host.Auth.LoginAsync(new Proto.LoginRequest { UserName = "anna", Password = Canary });
        var annaAgain = new Proto.AuthService.AuthServiceClient(server.Host.InvokerFor(login.Token));
        Assert.Equal("anna", (await annaAgain.MeAsync(new Proto.Empty())).UserName);

        var disable = await server.ToolAsync(key, "oadm_users_update_preview", new JsonObject { ["user"] = "anna", ["enabled"] = false });
        Assert.True(disable["ends_sessions"]!.GetValue<bool>());
        await server.ToolAsync(key, "oadm_users_update_run", new JsonObject { ["handle"] = disable["handle"]!.GetValue<string>() });
        Assert.Equal(StatusCode.Unauthenticated, (await Assert.ThrowsAsync<RpcException>(async () => await annaAgain.MeAsync(new Proto.Empty()))).StatusCode);

        var delete = await server.ToolAsync(key, "oadm_users_delete_preview", new JsonObject { ["user"] = "anna" });
        Assert.True(Card(server, "oadm_users_delete_preview", delete).Destructive);
        await server.ToolAsync(key, "oadm_users_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.Null(await UserAsync(server, "anna"));

        var audit = await AuditAsync(server);
        var agent = AccessKeyStore.AgentNameOf("Lobby-bot");
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "anna" && e.Detail == "role Viewer" && e.UserName == agent);
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "anna" && e.Detail == "password reset" && e.UserName == agent);
        Assert.Contains(audit, e => e.Action == AuditActions.UserChanged && e.Target == "anna" && e.Detail == "disabled");
        Assert.Contains(audit, e => e.Action == AuditActions.UserDeleted && e.Target == "anna" && e.UserName == agent);
        Assert.DoesNotContain(audit, e => string.Join('|', e.Target, e.Detail).Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARoleGainsRightsOnlyWithTheRunThePlanNamesThemAndTheAdministratorRoleNeverChanges()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();

        var admin = await server.CallToolAsync(key, "oadm_roles_save_preview", new JsonObject { ["role"] = "Administrator", ["revoke"] = new JsonArray("devices.refresh") });
        Assert.Contains(RoleStore.AdministratorFixed, Text(admin), StringComparison.Ordinal);
        var unknown = await server.CallToolAsync(key, "oadm_roles_save_preview", new JsonObject { ["name"] = "Agents", ["grant"] = new JsonArray("no.such.right") });
        Assert.Contains("grant[0]", unknown.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(RoleTools.UnknownPermission, Text(unknown), StringComparison.Ordinal);

        var create = await server.ToolAsync(key, "oadm_roles_save_preview", new JsonObject
        {
            ["name"] = "Agents", ["start_from"] = "Viewer", ["grant"] = new JsonArray(HostPermissionKeys.TasksDeleteAll),
        });
        Assert.Equal("create", create["change"]!.GetValue<string>());
        Assert.Contains("Delete all tasks", create["gains"]!.ToJsonString(), StringComparison.Ordinal);
        var matrix = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        Assert.DoesNotContain((await matrix.GetMatrixAsync(new Proto.Empty())).Roles, r => r.Name == "Agents"); // nothing written
        await server.ToolAsync(key, "oadm_roles_save_run", new JsonObject { ["handle"] = create["handle"]!.GetValue<string>() });
        var agents = (await matrix.GetMatrixAsync(new Proto.Empty())).Roles.Single(r => r.Name == "Agents");
        Assert.Contains(HostPermissionKeys.TasksDeleteAll, agents.Permissions);

        // A user with the role: the plan says who gets more rights; delete is refused while the user has it.
        await server.Host.Users.AddAsync(new Proto.AddUserRequest { UserName = "bob", Password = "pw", RoleId = agents.Id, AllDevices = true });
        var grant = await server.ToolAsync(key, "oadm_roles_save_preview", new JsonObject { ["role"] = "Agents", ["grant"] = new JsonArray(HostPermissionKeys.DevicesAddRemove) });
        Assert.Equal(1, grant["users"]!.GetValue<int>());
        Assert.Contains("1 user with the role Agents gets 1 more right", Warnings(grant), StringComparison.Ordinal);
        await server.ToolAsync(key, "oadm_roles_save_run", new JsonObject { ["handle"] = grant["handle"]!.GetValue<string>() });
        var inUse = await server.CallToolAsync(key, "oadm_roles_delete_preview", new JsonObject { ["role"] = "Agents" });
        Assert.Contains(RoleStore.InUse("Agents", 1), Text(inUse), StringComparison.Ordinal);
        var builtIn = await server.CallToolAsync(key, "oadm_roles_delete_preview", new JsonObject { ["role"] = "Operator" });
        Assert.Contains(RoleStore.BuiltIn("Operator"), Text(builtIn), StringComparison.Ordinal);

        await server.Host.Users.DeleteAsync(new Proto.UserIdRequest { Id = (await UserAsync(server, "bob"))!.Id });
        var delete = await server.ToolAsync(key, "oadm_roles_delete_preview", new JsonObject { ["role"] = "Agents" });
        Assert.True(Card(server, "oadm_roles_delete_preview", delete).Destructive);
        await server.ToolAsync(key, "oadm_roles_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        var audit = await AuditAsync(server);
        Assert.Contains(audit, e => e.Action == AuditActions.RoleCreated && e.Target == "Agents" && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == AuditActions.RoleChanged && e.Target == "Agents");
        Assert.Contains(audit, e => e.Action == AuditActions.RoleDeleted && e.Target == "Agents");

        var permissions = await server.ToolAsync(key, "oadm_roles_permissions", new JsonObject { ["search"] = "tasks.deleteAll" });
        Assert.Equal(HostPermissionKeys.TasksDeleteAll, Assert.Single(permissions["permissions"]!.AsArray())!["key"]!.GetValue<string>());
    }

    [Fact]
    public async Task ADeviceGroupIsSavedByTagNameOnlyWithTheRunAndItsDeleteNamesTheUsersWhoLoseTheirDevices()
    {
        await using var server = await McpTestServer.StartAsync();
        await GroupCallers.DeviceAsync(server.Host, 81, "Lobby");
        await GroupCallers.DeviceAsync(server.Host, 82, "Lobby");
        await GroupCallers.DeviceAsync(server.Host, 83, "Garage");
        var key = await server.CreateKeyAsync();

        var unknown = await server.CallToolAsync(key, "oadm_device_groups_save_preview", new JsonObject { ["name"] = "Lobby", ["tags"] = new JsonArray("Nope") });
        Assert.Contains(DeviceGroupTools.UnknownTag, Text(unknown), StringComparison.Ordinal);
        var badSerial = await server.CallToolAsync(key, "oadm_device_groups_save_preview", new JsonObject { ["name"] = "Lobby", ["serials"] = new JsonArray("xyz") });
        Assert.True(badSerial["isError"]!.GetValue<bool>());

        var preview = await server.ToolAsync(key, "oadm_device_groups_save_preview", new JsonObject { ["name"] = "Lobby", ["tags"] = new JsonArray("lobby") });
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        var groups = new Proto.DeviceGroupService.DeviceGroupServiceClient(server.Host.Invoker);
        Assert.Empty((await groups.ListAsync(new Proto.Empty())).Groups); // nothing written
        var saved = await server.ToolAsync(key, "oadm_device_groups_save_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(2, saved["devices"]!.GetValue<int>());
        var group = Assert.Single((await groups.ListAsync(new Proto.Empty())).Groups);

        await server.Host.Users.AddAsync(new Proto.AddUserRequest { UserName = "tech", Password = "pw", RoleId = BuiltInRoles.OperatorId.ToString(), DeviceGroupId = group.Id });
        var change = await server.ToolAsync(key, "oadm_device_groups_save_preview", new JsonObject { ["group"] = "Lobby", ["tags"] = new JsonArray("Lobby", "Garage") });
        Assert.Equal((2, 3), (change["devices_before"]!.GetValue<int>(), change["devices"]!.GetValue<int>()));
        Assert.Equal("tech", Assert.Single(change["users"]!.AsArray())!.GetValue<string>());
        Assert.Contains("1 user with this group sees 3 devices instead of 2", Warnings(change), StringComparison.Ordinal);
        await server.ToolAsync(key, "oadm_device_groups_save_run", new JsonObject { ["handle"] = change["handle"]!.GetValue<string>() });

        // A key with the group refuses its delete; without it the delete names the user who has no devices afterwards.
        await server.CreateKeyAsync(name: "lobby-key", roleId: BuiltInRoles.OperatorId.ToString(), groupId: group.Id);
        var refused = await server.CallToolAsync(key, "oadm_device_groups_delete_preview", new JsonObject { ["group"] = "Lobby" });
        Assert.Contains(AccessKeyTexts.GroupInUse("Lobby", ["lobby-key"]), Text(refused), StringComparison.Ordinal);
        var keys = new Proto.AccessKeyService.AccessKeyServiceClient(server.Host.Invoker);
        await keys.DeleteAsync(new Proto.AccessKeyIdRequest { Id = (await keys.ListAsync(new Proto.Empty())).Keys.Single(k => k.Name == "lobby-key").Id });
        var delete = await server.ToolAsync(key, "oadm_device_groups_delete_preview", new JsonObject { ["group"] = "lobby" });
        Assert.Equal("tech", Assert.Single(delete["users"]!.AsArray())!.GetValue<string>());
        Assert.True(Card(server, "oadm_device_groups_delete_preview", delete).Destructive);
        var deleted = await server.ToolAsync(key, "oadm_device_groups_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.Equal("tech", Assert.Single(deleted["users"]!.AsArray())!.GetValue<string>());
        var tech = (await UserAsync(server, "tech"))!;
        Assert.False(tech.AllDevices); // fail closed: no devices until an administrator chooses again
        Assert.Empty(tech.DeviceGroupId);
        var audit = await AuditAsync(server);
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupCreated && e.Target == "Lobby" && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupChanged && e.Target == "Lobby");
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceGroupDeleted && e.Target == "Lobby");
    }

    [Fact]
    public async Task ANewAccessKeyComesOnceThroughItsLinkNeverInAnAnswerOrTheAuditLogAndNeverOutlivesTheAgentsKey()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync(name: "Lobby-bot", days: 30);
        var other = await server.CreateKeyAsync(name: "other");
        var answers = new StringBuilder();

        var taken = await server.CallToolAsync(key, "oadm_access_keys_create_preview", new JsonObject { ["name"] = "other", ["role"] = "Viewer", ["devices"] = "all" });
        Assert.Contains(AccessKeyTexts.NameTaken, Text(taken), StringComparison.Ordinal);
        var days = await server.CallToolAsync(key, "oadm_access_keys_create_preview", new JsonObject { ["name"] = "new", ["role"] = "Viewer", ["devices"] = "all", ["days"] = 31 });
        Assert.Contains(AccessKeyTexts.ChooseDays, Text(days), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_access_keys_create_preview", new JsonObject { ["name"] = "Desk", ["role"] = "Administrator", ["days"] = 365 });
        answers.Append(preview.ToJsonString());
        Assert.Contains("The new key has the role Administrator and all devices", Warnings(preview), StringComparison.Ordinal);
        Assert.Contains("never outlives the agent's own key", Warnings(preview), StringComparison.Ordinal);
        var keys = new Proto.AccessKeyService.AccessKeyServiceClient(server.Host.Invoker);
        Assert.DoesNotContain((await keys.ListAsync(new Proto.Empty())).Keys, k => k.Name == "Desk"); // nothing written
        Assert.Equal("1 access key", Card(server, "oadm_access_keys_create_preview", preview).CountText);

        var run = await server.ToolAsync(key, "oadm_access_keys_create_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(run.ToJsonString());
        var link = run["key_link"]!;
        Assert.Equal(AccessKeyTools.KeyFileName, link["name"]!.GetValue<string>());
        Assert.Contains("-o oadm-key.txt", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var list = await keys.ListAsync(new Proto.Empty());
        var own = list.Keys.Single(k => k.Name == "Lobby-bot");
        var made = list.Keys.Single(k => k.Name == "Desk");
        Assert.Equal(own.Expires, made.Expires); // 365 days asked, the agent's own end given
        Assert.Equal(AccessKeyStore.AgentNameOf("Lobby-bot"), made.CreatedBy);

        // Another key's fetch answers like an ended link and leaves it; the agent's key fetches it once.
        var url = link["url"]!.GetValue<string>();
        using (var foreign = await SendAsync(server, HttpMethod.Get, other, url))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        string secret;
        using (var fetched = await SendAsync(server, HttpMethod.Get, key, url))
        {
            Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
            secret = await fetched.Content.ReadAsStringAsync();
        }

        Assert.StartsWith(AccessKeyStore.Prefix, secret, StringComparison.Ordinal);
        using (var twice = await SendAsync(server, HttpMethod.Get, key, url))
        {
            Assert.Equal(HttpStatusCode.NotFound, twice.StatusCode);
        }

        Assert.Contains("oadm_server_info", await server.ToolNamesAsync(secret)); // the new key works

        var audit = await AuditAsync(server);
        Assert.Contains(audit, e => e.Action == AuditActions.AccessKeyCreated && e.Target == "Desk" && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
        Assert.Contains(audit, e => e.Action == AuditActions.FileDownloaded && e.Target == AccessKeyTools.KeyFileName);
        var logs = McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory);
        foreach (var part in new[] { secret, secret[AccessKeyStore.Prefix.Length..], AuthTokenStore.HashOf(secret) })
        {
            Assert.DoesNotContain(part, answers.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail).Contains(part, StringComparison.Ordinal));
            Assert.DoesNotContain(part, logs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnAgentNeverDeletesItsOwnKeyAndAnotherKeyEndsAtOnce()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var other = await server.CreateKeyAsync(name: "other");

        var list = await server.ToolAsync(key, "oadm_access_keys_list");
        Assert.True(list["keys"]!.AsArray().Single(k => k!["name"]!.GetValue<string>() == "Lobby-bot")!["this_key"]!.GetValue<bool>());
        Assert.DoesNotContain(AuthTokenStore.HashOf(key), list.ToJsonString(), StringComparison.Ordinal);
        var self = await server.CallToolAsync(key, "oadm_access_keys_delete_preview", new JsonObject { ["key"] = "lobby-bot" });
        Assert.Contains(AccessKeyTexts.OwnKey, Text(self), StringComparison.Ordinal);

        // The service refuses it too, whatever reaches it.
        var keys = new Proto.AccessKeyService.AccessKeyServiceClient(server.Host.Invoker);
        var ownId = (await keys.ListAsync(new Proto.Empty())).Keys.Single(k => k.Name == "Lobby-bot").Id;
        var agent = (await server.Host.Get<AccessKeyStore>().ValidateAsync(key, CancellationToken.None))!;
        var caller = new AuthenticatedCaller(agent.Id, agent.AgentName, agent.RoleId, PermissionSet.All, "127.0.0.1", string.Empty, agent.TokenHash) { IsAccessKey = true };
        var direct = await Assert.ThrowsAsync<RpcException>(() => server.Host.Get<Oadm.Server.Auth.InProcessCalls>().CallAsync<Oadm.Server.Auth.AccessKeyGrpcService, Proto.AccessKeyIdRequest, Proto.AccessKeyList>(
            caller, AccessTools.KeyDeletePath, new Proto.AccessKeyIdRequest { Id = ownId }, (s, r, c) => s.Delete(r, c), CancellationToken.None));
        Assert.Equal(AccessKeyTexts.OwnKey, direct.Status.Detail);

        var preview = await server.ToolAsync(key, "oadm_access_keys_delete_preview", new JsonObject { ["key"] = "other" });
        Assert.True(Card(server, "oadm_access_keys_delete_preview", preview).Destructive);
        Assert.Contains("oadm_server_info", await server.ToolNamesAsync(other)); // nothing written
        await server.ToolAsync(key, "oadm_access_keys_delete_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        var ended = await server.PostAsync(other, McpTestServer.Request("tools/list"));
        Assert.Equal(401, ended.Status);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.AccessKeyDeleted && e.Target == "other" && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
    }
}
