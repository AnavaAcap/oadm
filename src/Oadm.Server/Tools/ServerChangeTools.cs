using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Contracts.Assistant;
using Oadm.Contracts.Mcp;
using Oadm.Contracts.Settings;
using Oadm.Server.Assistant;
using Oadm.Server.Auth;
using Oadm.Server.Licensing;
using Oadm.Server.Settings;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>The fields of a settings change a preview fixed: only these are written by the run, on top of what is stored then.</summary>
internal sealed record PlannedSettings(IReadOnlyDictionary<string, JsonElement> Fields);

/// <summary>The agents' server settings a preview fixed.</summary>
internal sealed record PlannedAgentServer(Proto.McpSettings Settings);

/// <summary>The assistant settings a preview fixed (the API key comes from the secret channel, never from the plan).</summary>
internal sealed record PlannedAssistant(Proto.AssistantSettings Settings);

/// <summary>A trial start a preview found possible.</summary>

/// <summary>
/// The reads and writes of the agents' own server: the MCP part of the page MCP &amp; Assistant. The tools never name the MCP
/// adapter, so the host hands them its <c>McpService</c> calls as functions (like <see cref="ServerStatusTool"/>).
/// </summary>
/// <param name="Get">The state as <c>McpService.Get</c> answers it to the caller.</param>
/// <param name="Set">Saves through <c>McpService.Set</c> as the caller (its checks and its audit).</param>
internal sealed record AgentServerCalls(Func<ToolCall, Task<Proto.McpState>> Get, Func<ToolCall, Proto.McpSettings, Task<Proto.McpState>> Set);

/// <summary>
/// The server's own settings as previews and runs (docs/spec/mcp.md "Server settings", decision 2026-11-15): the Settings
/// page's server fields (<c>SettingsService.Set</c>, a per-field allow-list, a diff of old and new values, the page's
/// checks and texts), the MCP server (<c>McpService.Set</c>), the assistant (<c>AssistantService.SetSettings</c> with the
/// API key through the secret channel, <c>Test</c> as a read) and the 30-day OADM PRO trial (<c>LicenseService.StartTrial</c>).
/// Every one is Administrator only, as in the client; the run writes through the gRPC service, which checks and audits again.
/// </summary>
internal static class ServerChangeTools
{
    public static readonly string SettingsGetPath = SettingsTools.GetPath;
    public static readonly string SettingsSetPath = InProcessCalls.PathOf(Proto.SettingsService.Descriptor, "Set");
    public static readonly string McpGetPath = InProcessCalls.PathOf(Proto.McpService.Descriptor, "Get");
    public static readonly string McpSetPath = InProcessCalls.PathOf(Proto.McpService.Descriptor, "Set");
    public static readonly string AssistantGetPath = InProcessCalls.PathOf(Proto.AssistantService.Descriptor, "GetSettings");
    public static readonly string AssistantSetPath = InProcessCalls.PathOf(Proto.AssistantService.Descriptor, "SetSettings");
    public static readonly string AssistantTestPath = InProcessCalls.PathOf(Proto.AssistantService.Descriptor, "Test");

    public const string NoChange = "Nothing would change: every value is the one OADM has.";
    public const string NameAChange = "Name at least one setting to change.";

    public const string BasicOverHttpWarning =
        "Devices that accept only Basic login then get their password over plain HTTP, which anyone on the network can read. Leave it off unless a device needs it.";

    public const string ListenUrlWarning =
        "The new listen URL takes effect when the server restarts. Clients, and the Assistant in them, that cannot reach the new address are cut off then; check that it is reachable first.";

    public const string VulnerabilityServerWarning = "The server fetches the list from the CVE records and NVD. It sends no device data.";
    public const string VulnerabilityClientsWarning = "Each client fetches the list from the CVE records and NVD. It sends no device data.";

    public const string McpOffWarning = "Agents can no longer connect until MCP is turned on again in OADM.";
    public const string McpPortWarning = "Agents must connect to the new port: change their configuration (Connect an agent).";
    public const string McpListenWarning = "Agents that reach the server through another interface can no longer connect.";
    public const string OwnConnectionWarning = "This agent connects through the MCP server you change: its connection may drop with the run.";

    public const string AssistantKeyWarning = "OADM keeps the API key encrypted and sends it only to the provider.";
    public const string AssistantOffWarning = "Turn off ends every chat of the Assistant, this one too.";
    public const string AssistantProviderWarning = "A new provider or address ends every chat of the Assistant.";
    public const string AssistantPicturesWarning = "Camera pictures then go to the provider when users ask the Assistant about them.";

    /// <summary>One field of the Settings page an agent may change: its name, label, schema, the page's check and how it reads and writes.</summary>
    private sealed record Field(
        string Name,
        string Label,
        JsonObject Schema,
        Func<JsonElement, string?> Check,
        Func<Proto.ServerSettings, JsonNode?> Read,
        Action<Proto.ServerSettings, JsonElement> Write);

    private static Field Whole(string name, string label, string description, int min, int max, Func<Proto.ServerSettings, int> read, Action<Proto.ServerSettings, int> write) =>
        new(name, label, Number(description, 0, 1_000_000),
            v => ServerSettingsRules.RangeError(v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null, min, max),
            s => read(s),
            (s, v) => write(s, v.GetInt32()));

    /// <summary>The Settings page's server fields, in the page's order (an allow-list: nothing else is written).</summary>
    private static readonly Field[] Fields =
    [
        new("server_name", "Server name", Text("The server's name, as clients show it.", ServerSettingsRules.MaxServerNameLength),
            v => ServerSettingsRules.ServerNameError(v.GetString()), s => s.ServerName, (s, v) => s.ServerName = v.GetString()!.Trim()),
        new("listen_url", "Listen URL", Text("Where the server listens for clients, e.g. https://0.0.0.0:5080. Takes effect after a restart.", 500),
            v => ServerSettingsRules.ListenUrlError(v.GetString()), s => s.ListenUrl, (s, v) => s.ListenUrl = v.GetString()!.Trim()),
        Whole("scan_parallelism", "Addresses scanned at once", "Addresses a network range scan checks at the same time.",
            ServerSettingsRules.MinScanParallelism, ServerSettingsRules.MaxScanParallelism, s => s.ScanParallelism, (s, v) => s.ScanParallelism = v),
        Whole("scan_timeout_ms", "Scan timeout (ms)", "How long a scan waits for an address, in milliseconds.",
            ServerSettingsRules.MinScanTimeoutMs, ServerSettingsRules.MaxScanTimeoutMs, s => s.ScanTimeoutMs, (s, v) => s.ScanTimeoutMs = v),
        Whole("zero_conf_seconds", "Discovery duration (s)", "How long a Discovery scan runs, in seconds.",
            ServerSettingsRules.MinZeroConfSeconds, ServerSettingsRules.MaxZeroConfSeconds, s => s.ZeroConfSeconds, (s, v) => s.ZeroConfSeconds = v),
        Whole("waiting_retry_minutes", "Try waiting devices every (min)", "How often OADM tries the waiting list again, in minutes.",
            ServerSettingsRules.MinWaitingRetryMinutes, ServerSettingsRules.MaxWaitingRetryMinutes, s => s.WaitingRetryMinutes, (s, v) => s.WaitingRetryMinutes = v),
        Whole("max_parallel_tasks_per_plugin", "Parallel tasks per plugin", "Devices a task runs on at the same time; applies to tasks that start later.",
            ServerSettingsRules.MinParallelTasks, ServerSettingsRules.MaxParallelTasks, s => s.MaxParallelTasksPerPlugin, (s, v) => s.MaxParallelTasksPerPlugin = v),
        Whole("clock_tolerance_seconds", "Allowed clock difference (s)", "How far a device clock may differ from the server's before it counts as off, in seconds.",
            ServerSettingsRules.MinClockTolerance, ServerSettingsRules.MaxClockTolerance, s => s.ClockToleranceSeconds, (s, v) => s.ClockToleranceSeconds = v),
        new("allow_basic_over_http", "Allow Basic login over HTTP", Flag("Devices that accept only Basic login may use it over plain HTTP. Off by default."),
            _ => null, s => s.AllowBasicOverHttp, (s, v) => s.AllowBasicOverHttp = v.GetBoolean()),
        new("vulnerability_update", "Update the list online", Choice("Who fetches the list of Axis CVEs online.", ServerSettingsRules.VulnerabilityUpdates),
            _ => null, s => s.VulnerabilityUpdate, (s, v) => s.VulnerabilityUpdate = v.GetString()!),
    ];

    public static IEnumerable<OadmTool> Create(ToolHandles handles, AgentServerCalls agentServer)
    {
        ArgumentNullException.ThrowIfNull(agentServer);

        // ---- server settings ------------------------------------------------------------------------------------------

        yield return new OadmTool(
            "oadm_settings_set_preview",
            "Preview a change of the server settings",
            "Shows the old and new value of each server setting you name, checked as the Settings page checks them; nothing is changed. Then call oadm_settings_set_run with the handle.",
            Object(null, [P("changes", Object("The settings to change; the others keep their value.", Fields.Select(f => P(f.Name, (JsonObject)f.Schema.DeepClone()))))], "changes"),
            OutObject(null,
            [
                P("changes", OutList("What changes, in the page's order.", OutObject(null,
                [
                    P("field", Out("string", "The setting.")),
                    P("label", Out("string", "Its name on the Settings page.")),
                    P("old", Out("string", "The value now (on or off for a switch).")),
                    P("new", Out("string", "The value after the run.")),
                ], "field", "label", "old", "new"))),
                P("will_change", Out("integer", "Settings that change.")),
                P("restart_needed", Out("boolean", "A changed listen URL takes effect only after the server restarts.")),
                .. ToolChanges.HandleOutput("oadm_settings_set_run"),
            ], ToolChanges.Required("changes", "will_change", "restart_needed")),
            [SettingsGetPath, SettingsSetPath],
            call => SettingsPreviewAsync(call, handles));

        yield return new OadmTool(
            "oadm_settings_set_run",
            "Change the server settings",
            "Saves the settings of a preview, checked again, as Save on the Settings page does; audited as server settings.",
            ToolChanges.RunInput("oadm_settings_set_preview"),
            OutObject(null,
            [
                P("saved", OutList("The settings saved with their value now.", OutObject(null,
                [
                    P("field", Out("string", "The setting.")),
                    P("value", Out("string", "Its value now.")),
                ], "field", "value"))),
            ], "saved"),
            [SettingsGetPath, SettingsSetPath],
            call => SettingsRunAsync(call, handles))
        {
            ReadsOnly = false,
            Destructive = true, // the old values are gone
            Idempotent = true,
        };

        // ---- the MCP server ---------------------------------------------------------------------------------------------

        yield return new OadmTool(
            "oadm_mcp_set_preview",
            "Preview a change of the MCP server",
            "Shows the MCP server's settings before and after (Turn on, Port, Listen on), checked as the card MCP checks them; nothing is changed. Then call oadm_mcp_set_run with the handle.",
            Object(null,
            [
                P("enabled", Flag("Turn the MCP server on (true) or off.")),
                P("port", Number("The port agents connect to, 1025 to 65535.", 0, 65_535)),
                P("listen_on", Text("The interface id from interfaces, or \"all\".", 200)),
            ]),
            OutObject(null,
            [
                P("now", AgentServerSchema("The settings now.")),
                P("after", AgentServerSchema("The settings after the run.")),
                P("status", Out("string", "What the card shows now, e.g. Running on port 5082.")),
                P("interfaces", OutList("The choices of Listen on.", OutObject(null,
                [
                    P("id", Out("string", "The id to pass as listen_on.")),
                    P("label", Out("string", "The interface with its addresses.")),
                ], "id", "label"))),
                P("will_change", Out("integer", "Settings that change.")),
                .. ToolChanges.HandleOutput("oadm_mcp_set_run"),
            ], ToolChanges.Required("now", "after", "status", "interfaces", "will_change")),
            [McpGetPath, McpSetPath],
            call => McpPreviewAsync(call, handles, agentServer));

        yield return new OadmTool(
            "oadm_mcp_set_run",
            "Change the MCP server",
            "Saves the MCP server's settings of a preview and opens or closes its port; audited like the card MCP.",
            ToolChanges.RunInput("oadm_mcp_set_preview"),
            OutObject(null,
            [
                P("settings", AgentServerSchema("The settings now.")),
                P("running", Out("boolean", "The port is open.")),
                P("status", Out("string", "What the card shows now.")),
            ], "settings", "running", "status"),
            [McpSetPath],
            call => McpRunAsync(call, handles, agentServer))
        {
            ReadsOnly = false,
            Destructive = true, // Turn off closes the port for every agent
            Idempotent = true,
        };

        // ---- the assistant ----------------------------------------------------------------------------------------------

        yield return new OadmTool(
            "oadm_assistant_settings_preview",
            "Preview a change of the Assistant settings",
            "Shows the Assistant settings before and after, checked as the card Assistant checks them; nothing is changed. A new API key is entered by the user through the secret channel, never in the chat. Then call oadm_assistant_settings_run with the handle.",
            Object(null,
            [
                P("enabled", Flag("Turn the Assistant on (true) or off.")),
                P("provider", Choice("The AI provider.", AssistantTexts.Providers)),
                P("address", Text("The provider's address (Ollama and OpenAI-compatible), e.g. http://10.0.0.5:11434.", 500)),
                P("new_api_key", Flag("The user enters a new API key for the run (through the secret channel).")),
                P("send_pictures", Flag("Camera pictures may go to the provider.")),
                P("daily_limit", Number("Tokens a day for the whole server.", 0, 2_000_000_000)),
                P("user_daily_limit", Number("Tokens a day per user; 0: no limit.", 0, 2_000_000_000)),
                P("steps_per_answer", Number("Tools the model may call for one message, 1 to 50.", 0, 1_000)),
                P("answer_seconds", Number("The longest one answer may take, 30 to 600 seconds.", 0, 100_000)),
            ]),
            OutObject(null,
            [
                P("now", AssistantSchema("The settings now.")),
                P("after", AssistantSchema("The settings after the run.")),
                P("will_change", Out("integer", "Settings that change.")),
                .. ToolChanges.HandleOutput("oadm_assistant_settings_run"),
                .. ToolSecretAnswers.OutputProperties(),
            ], ToolChanges.Required("now", "after", "will_change")),
            [AssistantGetPath, AssistantSetPath],
            call => AssistantPreviewAsync(call, handles));

        yield return new OadmTool(
            "oadm_assistant_settings_run",
            "Change the Assistant settings",
            "Saves the Assistant settings of a preview, checked again; audited like the card Assistant. The API key is never shown.",
            ToolChanges.RunInput("oadm_assistant_settings_preview"),
            OutObject(null,
            [
                P("settings", AssistantSchema("The settings now.")),
                P("status", Out("string", "On or Off.")),
            ], "settings", "status"),
            [AssistantSetPath],
            call => AssistantRunAsync(call, handles))
        {
            ReadsOnly = false,
            Destructive = true,
            Idempotent = true,
        };

        yield return new OadmTool(
            "oadm_assistant_test",
            "Test the Assistant's provider",
            "Asks the saved provider for its model list with the saved address and API key, as Test on the card Assistant does. Changes nothing.",
            Object(null, []),
            OutObject(null,
            [
                P("ok", Out("boolean", "The provider answered.")),
                P("message", Out("string", "What the test found, in plain words.")),
            ], "ok", "message"),
            [AssistantGetPath, AssistantTestPath],
            call => AssistantTestAsync(call))
        {
            ActsOutside = true, // it reaches the provider
        };
    }

    private static readonly string[] AgentServerFields = ["enabled", "port", "listen_on"];

    private static JsonObject AgentServerSchema(string description) => OutObject(description,
    [
        P("enabled", Out("boolean", "Turned on.")),
        P("port", Out("integer", "The port.")),
        P("listen_on", Out("string", "The interface id, or all.")),
    ], "enabled", "port", "listen_on");

    private static JsonObject AssistantSchema(string description) => OutObject(description,
    [
        P("enabled", Out("boolean", "Turned on.")),
        P("provider", Out("string", "The provider.")),
        P("address", Out("string", "The provider's address; empty when it needs none.")),
        P("has_api_key", Out("boolean", "An API key is saved (it is never shown).")),
        P("send_pictures", Out("boolean", "Camera pictures may go to the provider.")),
        P("daily_limit", Out("integer", "Tokens a day for the server.")),
        P("user_daily_limit", Out("integer", "Tokens a day per user; 0: no limit.")),
        P("steps_per_answer", Out("integer", "Tools per message.")),
        P("answer_seconds", Out("integer", "The longest answer, in seconds.")),
    ], "enabled", "provider", "address", "has_api_key", "send_pictures", "daily_limit", "user_daily_limit", "steps_per_answer", "answer_seconds");

    private static string TextOf(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "on" : "off",
        null => string.Empty,
        _ => value.ToJsonString().Trim('"'),
    };

    // ---- server settings ----------------------------------------------------------------------------------------------

    private static Task<Proto.ServerSettings> GetSettingsAsync(ToolCall call) =>
        call.CallAsync<SettingsGrpcService, Proto.Empty, Proto.ServerSettings>(SettingsGetPath, new Proto.Empty(), (s, r, c) => s.Get(r, c));

    private static async Task<JsonObject> SettingsPreviewAsync(ToolCall call, ToolHandles handles)
    {
        var given = call.Element("changes") is { ValueKind: JsonValueKind.Object } changes ? changes : default;
        var named = Fields.Where(f => given.ValueKind == JsonValueKind.Object && given.TryGetProperty(f.Name, out _)).ToList();
        if (named.Count == 0)
        {
            throw new ToolException(NameAChange, [new ToolFieldError("changes", NameAChange)]);
        }

        // The page's checks first, one error per field below it.
        var errors = named.Select(f => (f, Error: f.Check(given.GetProperty(f.Name)))).Where(e => e.Error is not null)
            .Select(e => new ToolFieldError("changes." + e.f.Name, e.Error!)).ToList();
        if (errors.Count > 0)
        {
            throw new ToolException(ToolAnswers.FieldsMessage, errors);
        }

        var current = await GetSettingsAsync(call).ConfigureAwait(false);
        var after = current.Clone();
        var rows = new JsonArray();
        var planned = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var field in named)
        {
            var value = given.GetProperty(field.Name).Clone();
            field.Write(after, value);
            var old = field.Read(current);
            var now = field.Read(after);
            if (JsonNode.DeepEquals(old, now))
            {
                continue;
            }

            planned[field.Name] = value;
            rows.Add(new JsonObject { ["field"] = field.Name, ["label"] = field.Label, ["old"] = TextOf(old), ["new"] = TextOf(now) });
            switch (field.Name)
            {
                case "allow_basic_over_http" when after.AllowBasicOverHttp:
                    warnings.Add(BasicOverHttpWarning);
                    break;
                case "listen_url":
                    warnings.Add(ListenUrlWarning);
                    break;
                case "vulnerability_update" when after.VulnerabilityUpdate == "server":
                    warnings.Add(VulnerabilityServerWarning);
                    break;
                case "vulnerability_update" when after.VulnerabilityUpdate == "clients":
                    warnings.Add(VulnerabilityClientsWarning);
                    break;
            }
        }

        if (planned.Count == 0)
        {
            warnings.Add(NoChange);
        }

        var answer = new JsonObject
        {
            ["changes"] = rows,
            ["will_change"] = planned.Count,
            ["restart_needed"] = planned.ContainsKey("listen_url"),
        };
        return ToolChanges.WithHandle(answer, call, handles, "oadm_settings_set_run", planned.Count == 0 ? null : new PlannedSettings(planned), warnings);
    }

    private static async Task<JsonObject> SettingsRunAsync(ToolCall call, ToolHandles handles)
    {
        var plan = ToolChanges.Take<PlannedSettings>(call, handles);

        // Only the planned fields, on top of what is stored now; the service checks every value again and audits the change.
        var request = await GetSettingsAsync(call).ConfigureAwait(false);
        foreach (var field in Fields.Where(f => plan.Fields.ContainsKey(f.Name)))
        {
            field.Write(request, plan.Fields[field.Name]);
        }

        var saved = await call.CallAsync<SettingsGrpcService, Proto.ServerSettings, Proto.ServerSettings>(SettingsSetPath, request, (s, r, c) => s.Set(r, c)).ConfigureAwait(false);
        return new JsonObject
        {
            ["saved"] = new JsonArray([.. Fields.Where(f => plan.Fields.ContainsKey(f.Name)).Select(f => (JsonNode)new JsonObject { ["field"] = f.Name, ["value"] = TextOf(f.Read(saved)) })]),
        };
    }

    // ---- the MCP server -----------------------------------------------------------------------------------------------

    private static JsonObject AgentServerJson(Proto.McpSettings settings) => new()
    {
        ["enabled"] = settings.Enabled,
        ["port"] = settings.Port,
        ["listen_on"] = InterfaceIdOf(settings.ListenOn),
    };

    private static string InterfaceIdOf(string? listenOn) => Sdk.Network.InterfaceOptions.IsAll(listenOn) ? Sdk.Network.InterfaceOptions.AllId : listenOn!.Trim();

    private static async Task<JsonObject> McpPreviewAsync(ToolCall call, ToolHandles handles, AgentServerCalls calls)
    {
        var state = await calls.Get(call).ConfigureAwait(false);
        var now = state.Settings ?? new Proto.McpSettings();
        var after = new Proto.McpSettings
        {
            Enabled = call.Flag("enabled") ?? now.Enabled,
            Port = call.Number("port") ?? now.Port,
            ListenOn = call.Text("listen_on") is { } listen ? InterfaceIdOf(listen) : InterfaceIdOf(now.ListenOn),
        };

        var errors = new List<ToolFieldError>();
        if (after.Port is < 1025 or > 65_535)
        {
            errors.Add(new ToolFieldError("port", McpTexts.PortRange));
        }

        if (!Sdk.Network.InterfaceOptions.IsAll(after.ListenOn) && !state.Interfaces.Any(i => i.Id == after.ListenOn)
            && !string.Equals(after.ListenOn, InterfaceIdOf(now.ListenOn), StringComparison.Ordinal))
        {
            errors.Add(new ToolFieldError("listen_on", McpTexts.UnknownInterface));
        }

        if (after.Enabled && !state.PkiAvailable)
        {
            errors.Add(new ToolFieldError("enabled", McpTexts.NoPki));
        }

        if (errors.Count > 0)
        {
            throw new ToolException(errors.Count == 1 ? errors[0].Error : ToolAnswers.FieldsMessage, errors);
        }

        var nowJson = AgentServerJson(now);
        var afterJson = AgentServerJson(after);
        var changed = AgentServerFields.Count(k => !JsonNode.DeepEquals(nowJson[k], afterJson[k]));
        var warnings = new List<string>();
        if (now.Enabled && !after.Enabled)
        {
            warnings.Add(McpOffWarning);
        }

        if (after.Enabled && after.Port != now.Port)
        {
            warnings.Add(McpPortWarning);
        }

        if (after.Enabled && !string.Equals(afterJson["listen_on"]!.GetValue<string>(), nowJson["listen_on"]!.GetValue<string>(), StringComparison.Ordinal))
        {
            warnings.Add(McpListenWarning);
        }

        if (changed > 0 && warnings.Count > 0 && call.Caller.Channel.KeyName is not null)
        {
            warnings.Add(OwnConnectionWarning); // an agent that changes the server it came through
        }

        if (changed == 0)
        {
            warnings.Add(NoChange);
        }

        var answer = new JsonObject
        {
            ["now"] = nowJson,
            ["after"] = afterJson,
            ["status"] = state.Status,
            ["interfaces"] = new JsonArray([.. state.Interfaces.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["label"] = i.Label })]),
            ["will_change"] = changed,
        };
        return ToolChanges.WithHandle(answer, call, handles, "oadm_mcp_set_run", changed == 0 ? null : new PlannedAgentServer(after), warnings);
    }

    private static async Task<JsonObject> McpRunAsync(ToolCall call, ToolHandles handles, AgentServerCalls calls)
    {
        var plan = ToolChanges.Take<PlannedAgentServer>(call, handles);
        var state = await calls.Set(call, plan.Settings.Clone()).ConfigureAwait(false);
        return new JsonObject
        {
            ["settings"] = AgentServerJson(state.Settings ?? plan.Settings),
            ["running"] = state.Running,
            ["status"] = state.Status,
        };
    }

    // ---- the assistant ------------------------------------------------------------------------------------------------

    private static JsonObject AssistantJson(Proto.AssistantSettings settings, bool hasKey) => new()
    {
        ["enabled"] = settings.Enabled,
        ["provider"] = settings.Provider,
        ["address"] = settings.Address,
        ["has_api_key"] = hasKey,
        ["send_pictures"] = settings.SendPictures,
        ["daily_limit"] = settings.DailyLimit,
        ["user_daily_limit"] = settings.UserDailyLimit,
        ["steps_per_answer"] = settings.StepsPerAnswer,
        ["answer_seconds"] = settings.AnswerSeconds,
    };

    private static Task<Proto.AssistantSettingsState> GetAssistantAsync(ToolCall call) =>
        call.CallAsync<AssistantGrpcService, Proto.Empty, Proto.AssistantSettingsState>(AssistantGetPath, new Proto.Empty(), (s, r, c) => s.GetSettings(r, c));

    private static async Task<JsonObject> AssistantPreviewAsync(ToolCall call, ToolHandles handles)
    {
        var state = await GetAssistantAsync(call).ConfigureAwait(false);
        var now = state.Settings ?? new Proto.AssistantSettings();
        var after = now.Clone();
        after.ApiKey = string.Empty;
        after.Enabled = call.Flag("enabled") ?? now.Enabled;
        after.Provider = call.Text("provider")?.Trim() ?? now.Provider;
        after.SendPictures = call.Flag("send_pictures") ?? now.SendPictures;
        after.DailyLimit = call.Number("daily_limit") ?? now.DailyLimit;
        after.UserDailyLimit = call.Number("user_daily_limit") ?? now.UserDailyLimit;
        after.StepsPerAnswer = call.Number("steps_per_answer") ?? now.StepsPerAnswer;
        after.AnswerSeconds = call.Number("answer_seconds") ?? now.AnswerSeconds;
        var needsAddress = after.Provider is AssistantTexts.Ollama or AssistantTexts.OpenAiCompatible;
        after.Address = needsAddress ? call.Text("address")?.Trim() ?? now.Address : string.Empty;
        var newKey = call.Flag("new_api_key") == true;

        // The card's checks, with its texts, below each field.
        var errors = new List<ToolFieldError>();
        if (!AssistantTexts.Providers.Contains(after.Provider))
        {
            errors.Add(new ToolFieldError("provider", AssistantTexts.ChooseProvider));
        }

        if (needsAddress && after.Address.Length == 0)
        {
            errors.Add(new ToolFieldError("address", AssistantTexts.EnterAddress));
        }
        else if (needsAddress && (!Uri.TryCreate(after.Address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)))
        {
            errors.Add(new ToolFieldError("address", AssistantTexts.AddressNotValid));
        }

        // The card's key rules: the saved key goes only to the provider and address it was saved for; Turn on with Anthropic or
        // OpenAI needs one (an OpenAI-compatible server may take none); Ollama takes none.
        var takesKey = AssistantSettings.NeedsApiKey(after.Provider);
        var sameTarget = now.Provider == after.Provider && string.Equals(now.Address, after.Address, StringComparison.Ordinal);
        var keyKept = takesKey && state.HasApiKey && sameTarget;
        newKey &= takesKey;
        if (!newKey && takesKey && ((!sameTarget && state.HasApiKey) || (after.Enabled && after.Provider is AssistantTexts.Anthropic or AssistantTexts.OpenAi && !keyKept)))
        {
            errors.Add(new ToolFieldError("new_api_key", AssistantTexts.EnterApiKey));
        }

        if (after.DailyLimit is < AssistantTexts.MinDailyLimit or > AssistantTexts.MaxDailyLimit)
        {
            errors.Add(new ToolFieldError("daily_limit", AssistantTexts.DailyLimitRange));
        }

        if (after.UserDailyLimit != 0 && after.UserDailyLimit is < AssistantTexts.MinDailyLimit or > AssistantTexts.MaxDailyLimit)
        {
            errors.Add(new ToolFieldError("user_daily_limit", AssistantTexts.UserDailyLimitRange));
        }

        if (after.StepsPerAnswer is < AssistantTexts.MinStepsPerAnswer or > AssistantTexts.MaxStepsPerAnswer)
        {
            errors.Add(new ToolFieldError("steps_per_answer", AssistantTexts.StepsRange));
        }

        if (after.AnswerSeconds is < AssistantTexts.MinAnswerSeconds or > AssistantTexts.MaxAnswerSeconds)
        {
            errors.Add(new ToolFieldError("answer_seconds", AssistantTexts.AnswerSecondsRange));
        }

        if (errors.Count > 0)
        {
            throw new ToolException(errors.Count == 1 ? errors[0].Error : ToolAnswers.FieldsMessage, errors);
        }

        var nowJson = AssistantJson(now, state.HasApiKey);
        var afterJson = AssistantJson(after, newKey || keyKept);
        var changed = afterJson.Count(p => p.Key != "has_api_key" && !JsonNode.DeepEquals(p.Value, nowJson[p.Key]))
            + (newKey || state.HasApiKey != (newKey || keyKept) ? 1 : 0); // a new key, or a key dropped

        var warnings = new List<string>();
        if (now.Enabled && !after.Enabled)
        {
            warnings.Add(AssistantOffWarning);
        }
        else if (now.Enabled && !sameTarget)
        {
            warnings.Add(AssistantProviderWarning);
        }

        if (after.SendPictures && !now.SendPictures)
        {
            warnings.Add(AssistantPicturesWarning);
        }

        if (newKey)
        {
            warnings.Add(AssistantKeyWarning);
        }

        if (changed <= 0)
        {
            warnings.Add(NoChange);
        }

        var answer = new JsonObject { ["now"] = nowJson, ["after"] = afterJson, ["will_change"] = Math.Max(0, changed) };
        ToolChanges.WithHandle(answer, call, handles, "oadm_assistant_settings_run", changed <= 0 ? null : new PlannedAssistant(after), warnings);
        if (newKey && answer["handle"]?.GetValue<string>() is { } handle && call.SecretStore is not null)
        {
            ToolSecretAnswers.Declare(call, answer, handle, "oadm_assistant_settings_run", [new ToolSecretSlot("api_key", "API key", 500)]);
        }
        else
        {
            answer["needs_secrets"] = new JsonArray();
            answer["secret_links"] = null;
        }

        return answer;
    }

    private static async Task<JsonObject> AssistantRunAsync(ToolCall call, ToolHandles handles)
    {
        var plan = ToolChanges.Take<PlannedAssistant>(call, handles);
        var request = plan.Settings.Clone();
        request.ApiKey = call.Secret("api_key") ?? string.Empty; // empty keeps the saved key; never in an answer
        var state = await call.CallAsync<AssistantGrpcService, Proto.AssistantSettings, Proto.AssistantSettingsState>(AssistantSetPath, request, (s, r, c) => s.SetSettings(r, c)).ConfigureAwait(false);
        return new JsonObject { ["settings"] = AssistantJson(state.Settings, state.HasApiKey), ["status"] = state.Status };
    }

    private static async Task<JsonObject> AssistantTestAsync(ToolCall call)
    {
        var state = await GetAssistantAsync(call).ConfigureAwait(false);
        var settings = state.Settings.Clone();
        settings.ApiKey = string.Empty; // the saved key
        var reply = await call.CallAsync<AssistantGrpcService, Proto.AssistantSettings, Proto.AssistantTestReply>(AssistantTestPath, settings, (s, r, c) => s.Test(r, c)).ConfigureAwait(false);
        return new JsonObject { ["ok"] = reply.Ok, ["message"] = reply.Message };
    }
}
