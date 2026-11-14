using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Devices;
using Oadm.Server.Settings;

using static Oadm.Server.Mcp.McpSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Mcp;

/// <summary>
/// <c>oadm_server_info</c>, the first call (docs/spec/mcp.md "Tools"): the server's version, the key's user, role and what it
/// allows, Read only, the limits and how many devices the user sees. Through <c>AuthService.Me</c>, <c>SettingsService.Get</c>
/// and <c>DeviceService.List</c>.
/// </summary>
internal static class ServerInfoTool
{
    public static readonly string MePath = InProcessCalls.PathOf(Proto.AuthService.Descriptor, "Me");
    public static readonly string SettingsPath = InProcessCalls.PathOf(Proto.SettingsService.Descriptor, "Get");

    public static McpTool Create(Func<PermissionCatalog> catalog) => new(
        "oadm_server_info",
        "Server info",
        "The OADM server, your user, role and what it allows, the limits of this server and how many devices you see. Call it first.",
        Object(null, []),
        OutObject(null,
        [
            P("server", OutObject(null,
            [
                P("name", Out("string", "The product.")),
                P("version", Out("string", "The server's version.")),
            ], "name", "version")),
            P("user", Out("string", "The user of the access key.")),
            P("role", Out("string", "The user's role.")),
            P("all_permissions", Out("boolean", "The role allows everything (Administrator).")),
            P("permissions", OutList("What the role allows.", OutObject(null,
            [
                P("key", Out("string", "The permission.")),
                P("name", Out("string", "Its name in OADM.")),
            ], "key", "name"))),
            P("devices", OutObject("The devices the user sees.", [
                P("count", Out("integer", "How many.")),
                P("scope", Out("string", "All devices, or the user's device group.")),
            ], "count", "scope")),
            P("access_key", OutObject(null,
            [
                P("name", Out("string", "The key's name.")),
                P("read_only", Out("boolean", "The key only reads.")),
                P("expires", Out("string", "When it ends (UTC).")),
            ], "name", "read_only", "expires")),
            P("limits", OutObject(null,
            [
                P("calls_per_second", Out("integer", "Calls per second and key.")),
                P("runs_per_minute", Out("integer", "Task runs per minute and key.")),
                P("rows_default", Out("integer", "Rows of a list page by default.")),
                P("rows_max", Out("integer", "Rows of a list page at most.")),
                P("answer_max_bytes", Out("integer", "The largest answer.")),
            ], "calls_per_second", "runs_per_minute", "rows_default", "rows_max", "answer_max_bytes")),
        ], "server", "user", "role", "all_permissions", "permissions", "devices", "access_key", "limits"),
        [MePath, SettingsPath, DeviceTools.ListPath],
        call => RunAsync(call, catalog()));

    private static async Task<JsonObject> RunAsync(McpToolCall call, PermissionCatalog catalog)
    {
        var me = await call.CallAsync<AuthGrpcService, Proto.Empty, Proto.UserInfo>(MePath, new Proto.Empty(), (s, r, c) => s.Me(r, c)).ConfigureAwait(false);
        var settings = await call.CallAsync<SettingsGrpcService, Proto.Empty, Proto.ServerSettings>(SettingsPath, new Proto.Empty(), (s, r, c) => s.Get(r, c)).ConfigureAwait(false);
        var devices = await call.CallAsync<DeviceGrpcService, Proto.Empty, Proto.DeviceList>(DeviceTools.ListPath, new Proto.Empty(), (s, r, c) => s.List(r, c)).ConfigureAwait(false);
        var keys = me.AllPermissions ? catalog.Entries.Select(e => e.Key) : me.Permissions;
        var permissions = new JsonArray();
        foreach (var key in keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            permissions.Add(new JsonObject { ["key"] = key, ["name"] = catalog.Find(key) is { } entry ? Name(entry) : key });
        }

        var scope = me.AllDevices || !me.HasAllDevices ? "All devices"
            : me.DeviceGroupName is { Length: > 0 } group ? group
            : "No devices";
        return new JsonObject
        {
            ["server"] = new JsonObject { ["name"] = "OADM", ["version"] = settings.ServerVersion },
            ["user"] = me.UserName,
            ["role"] = me.RoleName,
            ["all_permissions"] = me.AllPermissions,
            ["permissions"] = permissions,
            ["devices"] = new JsonObject { ["count"] = devices.Devices.Count, ["scope"] = scope },
            ["access_key"] = new JsonObject
            {
                ["name"] = call.Caller.KeyName,
                ["read_only"] = call.Caller.ReadOnly,
                ["expires"] = call.Caller.KeyExpiresUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            },
            ["limits"] = new JsonObject
            {
                ["calls_per_second"] = McpAccess.CallsPerSecond,
                ["runs_per_minute"] = McpAccess.RunsPerMinute,
                ["rows_default"] = McpAnswers.DefaultRows,
                ["rows_max"] = McpAnswers.MaxRows,
                ["answer_max_bytes"] = McpAnswers.MaxBytes,
            },
        };
    }

    /// <summary>"Tasks: Cancel tasks", "Applications: Install" (the matrix's group and name).</summary>
    private static string Name(PermissionEntry entry) => entry.Group + ": " + entry.Name;
}
