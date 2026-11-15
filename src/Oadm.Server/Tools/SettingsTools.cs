using System.Text.Json.Nodes;

using Oadm.Server.Auth;
using Oadm.Server.Settings;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>
/// The server settings a user may read (docs/spec/mcp.md "Other areas"): what the client's Settings page shows every user,
/// read only. Through <c>SettingsService.Get</c>. Never a secret: the credential list, the notices of the server, the
/// server's id and its license stay in OADM; settings are never changed over MCP (rule 5).
/// </summary>
internal static class SettingsTools
{
    public static readonly string GetPath = InProcessCalls.PathOf(Proto.SettingsService.Descriptor, "Get");

    public static IEnumerable<OadmTool> Create()
    {
        yield return new OadmTool(
            "oadm_settings_get",
            "Server settings",
            "The server settings OADM shows every user, read only: how it scans, retries, runs tasks and checks clocks. Change them in OADM.",
            Object(null, []),
            OutObject(null,
            [
                P("server_name", Out("string", "The name of this server.")),
                P("listen_url", Out("string", "The address the server listens on for clients.")),
                P("scan", OutObject("How OADM scans a network range.",
                [
                    P("parallelism", Out("integer", "Addresses checked at the same time.")),
                    P("timeout_ms", Out("integer", "How long OADM waits for an address, in milliseconds.")),
                ], "parallelism", "timeout_ms")),
                P("zero_conf_seconds", Out("integer", "How long a Discovery scan runs, in seconds.")),
                P("waiting_retry_minutes", Out("integer", "How often OADM tries the waiting list again, in minutes.")),
                P("max_parallel_tasks_per_plugin", Out("integer", "Devices a task runs on at the same time.")),
                P("allow_basic_over_http", Out("boolean", "Devices that accept only Basic login may use it over plain HTTP.")),
                P("clock_tolerance_seconds", Out("integer", "How far a device clock may differ from the server's before it counts as off, in seconds.")),
                P("vulnerability_update", Out("string", "Who fetches the list of Axis CVEs online: off, server or clients.")),
            ], "server_name", "listen_url", "scan", "zero_conf_seconds", "waiting_retry_minutes", "max_parallel_tasks_per_plugin",
                "allow_basic_over_http", "clock_tolerance_seconds", "vulnerability_update"),
            [GetPath],
            call => GetAsync(call));
    }

    private static async Task<JsonObject> GetAsync(ToolCall call)
    {
        var settings = await call.CallAsync<SettingsGrpcService, Proto.Empty, Proto.ServerSettings>(GetPath, new Proto.Empty(), (s, r, c) => s.Get(r, c)).ConfigureAwait(false);
        return new JsonObject
        {
            ["server_name"] = settings.ServerName,
            ["listen_url"] = settings.ListenUrl,
            ["scan"] = new JsonObject { ["parallelism"] = settings.ScanParallelism, ["timeout_ms"] = settings.ScanTimeoutMs },
            ["zero_conf_seconds"] = settings.ZeroConfSeconds,
            ["waiting_retry_minutes"] = settings.WaitingRetryMinutes,
            ["max_parallel_tasks_per_plugin"] = settings.MaxParallelTasksPerPlugin,
            ["allow_basic_over_http"] = settings.AllowBasicOverHttp,
            ["clock_tolerance_seconds"] = settings.ClockToleranceSeconds,
            ["vulnerability_update"] = settings.VulnerabilityUpdate,
        };
    }
}
