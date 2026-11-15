using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Settings;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>
/// <c>oadm_server_info</c>, the first call (docs/spec/mcp.md "Tools"): the server's version, the key's name, role and what it
/// allows, the limits and how many devices it sees. Through <c>AuthService.Me</c>, <c>SettingsService.Get</c>
/// and <c>DeviceService.Search</c> (the count only).
/// </summary>
internal static class ServerInfoTool
{
    public static readonly string MePath = InProcessCalls.PathOf(Proto.AuthService.Descriptor, "Me");
    public static readonly string SettingsPath = InProcessCalls.PathOf(Proto.SettingsService.Descriptor, "Get");

    public static OadmTool Create(Func<PermissionCatalog> catalog) => new(
        "oadm_server_info",
        "Server info",
        "The OADM server, who you are, your role and what it allows, the limits of this server and how many devices you see. Call it first.",
        Object(null, []),
        OutObject(null,
        [
            P("server", OutObject(null,
            [
                P("name", Out("string", "The product.")),
                P("version", Out("string", "The server's version.")),
            ], "name", "version")),
            P("user", Out("string", "Who you are in OADM: the name of the audit log and of your tasks.")),
            P("role", Out("string", "Your role.")),
            P("all_permissions", Out("boolean", "The role allows everything (Administrator).")),
            P("permissions", OutList("What the role allows.", OutObject(null,
            [
                P("key", Out("string", "The permission.")),
                P("name", Out("string", "Its name in OADM.")),
            ], "key", "name"))),
            P("devices", OutObject("The devices you see.", [
                P("count", Out("integer", "How many.")),
                P("scope", Out("string", "All devices, or your device group.")),
            ], "count", "scope")),
            P("access_key", OrNull(OutObject("The access key of the call; null without one.",
            [
                P("name", Out("string", "The key's name.")),
                P("expires", Out("string", "When it ends (UTC).")),
            ], "name", "expires"))),
            P("limits", OutObject(null,
            [
                P("calls_per_second", OrNull(Out("integer", "Calls per second; null without a limit."))),
                P("calls_at_once", OrNull(Out("integer", "Calls that wait for their answer at the same time; null without a limit."))),
                P("changes_per_minute", OrNull(Out("integer", "Changes per minute (runs, task starts, calls that act outside OADM); null without a limit."))),
                P("rows_default", Out("integer", "Rows of a list page by default.")),
                P("rows_max", Out("integer", "Rows of a list page at most.")),
                P("answer_max_bytes", Out("integer", "The largest answer.")),
            ], "calls_per_second", "calls_at_once", "changes_per_minute", "rows_default", "rows_max", "answer_max_bytes")),
        ], "server", "user", "role", "all_permissions", "permissions", "devices", "access_key", "limits"),
        [MePath, SettingsPath, DeviceTools.SearchPath],
        call => RunAsync(call, catalog()));

    private static async Task<JsonObject> RunAsync(ToolCall call, PermissionCatalog catalog)
    {
        var me = await call.CallAsync<AuthGrpcService, Proto.Empty, Proto.UserInfo>(MePath, new Proto.Empty(), (s, r, c) => s.Me(r, c)).ConfigureAwait(false);
        var settings = await call.CallAsync<SettingsGrpcService, Proto.Empty, Proto.ServerSettings>(SettingsPath, new Proto.Empty(), (s, r, c) => s.Get(r, c)).ConfigureAwait(false);
        var devices = await DeviceTools.SearchAsync(call, new Proto.SearchDevicesRequest()).ConfigureAwait(false);
        var channel = call.Caller.Channel; // what the channel tells of its key and its own limits
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
            ["devices"] = new JsonObject { ["count"] = devices.Total, ["scope"] = scope },
            ["access_key"] = channel.KeyName is { } keyName
                ? new JsonObject
                {
                    ["name"] = keyName,
                    ["expires"] = channel.KeyExpiresUtc?.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                }
                : null,
            ["limits"] = new JsonObject
            {
                ["calls_per_second"] = channel.CallsPerSecond,
                ["calls_at_once"] = channel.CallsAtOnce,
                ["changes_per_minute"] = channel.ChangesPerMinute,
                ["rows_default"] = ToolAnswers.DefaultRows,
                ["rows_max"] = ToolAnswers.MaxRows,
                ["answer_max_bytes"] = ToolAnswers.MaxBytes,
            },
        };
    }

    /// <summary>"Tasks: Cancel tasks", "Applications: Install" (the matrix's group and name).</summary>
    private static string Name(PermissionEntry entry) => entry.Group + ": " + entry.Name;
}
