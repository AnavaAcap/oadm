using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Server.Auth;

namespace Oadm.Server.Tools;

/// <summary>
/// Who calls a tool (docs/spec/mcp.md "Tools and MCP"): the user with the role and devices of now, passed to every tool as a
/// parameter. The channel that brings the call makes it: MCP from the access key, a later built-in assistant from the
/// logged-in user. A tool never reads anything else of the request.
/// </summary>
/// <param name="User">The caller with the role, the permissions and the device view of now (an MCP access key: "MCP Lobby-bot").</param>
/// <param name="BindingKey">What cursors and handles are bound to (never sent anywhere): the access key's hash, a session's token hash.</param>
public sealed record ToolCaller(AuthenticatedCaller User, string BindingKey)
{
    /// <summary>What the channel tells about itself in <c>oadm_server_info</c> (its key and limits); <see cref="ToolChannel.None"/> without.</summary>
    public ToolChannel Channel { get; init; } = ToolChannel.None;

    /// <summary>
    /// The devices the user selected in the client's device grid, cut to the caller's device view (the assistant sends them with
    /// every message); null over MCP, where nothing is selected (<see cref="SelectionTools"/>).
    /// </summary>
    public IReadOnlyList<string>? Selection { get; init; }
}

/// <summary>What <c>oadm_server_info</c> says of the channel a caller came through: its access key and its own limits.</summary>
/// <param name="KeyName">The name of the access key; null without one.</param>
/// <param name="KeyExpiresUtc">When the key ends.</param>
/// <param name="CallsPerSecond">Calls a second the channel takes per caller; null without a limit.</param>
/// <param name="CallsAtOnce">Calls waiting for their answer at once; null without a limit.</param>
/// <param name="ChangesPerMinute">Changes a minute (runs, task starts, calls that act outside OADM); null without a limit.</param>
/// <param name="FilesUrl">Where the channel takes files of upload links ("https://server:5082/files/"); null: it takes none.</param>
/// <param name="SecretsUrl">Where the channel takes the values of secret links ("https://server:5082/secrets/"); null: it takes none (the assistant's card takes them).</param>
public sealed record ToolChannel(string? KeyName = null, DateTime? KeyExpiresUtc = null, int? CallsPerSecond = null, int? CallsAtOnce = null, int? ChangesPerMinute = null, string? FilesUrl = null,
    string? SecretsUrl = null)
{
    public static ToolChannel None { get; } = new();
}

/// <summary>
/// One tool (docs/spec/mcp.md "Tools"): a user action, named <c>oadm_&lt;area&gt;_&lt;action&gt;</c>, with a title, a short
/// description, a strict input schema, an output schema and honest annotations. <see cref="Methods"/> are the gRPC methods it
/// calls (its permission): it is listed only for a caller who may call all of them, so the list follows the role, the device
/// group and the plugins of now. It knows nothing of the channel that calls it.
/// </summary>
internal sealed record OadmTool(
    string Name,
    string Title,
    string Description,
    JsonObject Input,
    JsonObject Output,
    IReadOnlyList<string> Methods,
    Func<ToolCall, Task<JsonObject>> Run)
{
    /// <summary>Reads only (<c>readOnlyHint</c>); a change is a preview tool (reads) and a run tool (changes).</summary>
    public bool ReadsOnly { get; init; } = true;

    public bool Destructive { get; init; }

    public bool Idempotent { get; init; } = true;

    /// <summary>The tool starts tasks (a channel may limit these runs per minute).</summary>
    public bool StartsTasks { get; init; }

    /// <summary>
    /// The call reaches devices or the network although it writes nothing to OADM (a scan that logs in with the credential
    /// list): it is never announced as a plain read, and a channel counts it like a change.
    /// </summary>
    public bool ActsOutside { get; init; }

    /// <summary>
    /// The call takes a camera picture, and every picture is audited: a channel counts it against its own limit of pictures a
    /// minute so that a low-rights caller cannot fill the audit log.
    /// </summary>
    public bool CountsAsPicture { get; init; }

    /// <summary>A channel counts this call against its limit of changes (a run, a task start, a call that acts outside).</summary>
    public bool CountsAsChange => !ReadsOnly || StartsTasks || ActsOutside;

    /// <summary>
    /// A rule of the tool beyond the gRPC methods it calls (a plugin tool: the plugin is on, the method's permission): the reason
    /// the caller may not use it now, null when it may. Asked when the list is made and before every call.
    /// </summary>
    public Func<ToolCaller, string?>? Gate { get; init; }
}

/// <summary>
/// Tools that change while the server runs (the tools of the plugins that are on): asked for their tools each time the registry
/// reads them, so a plugin turned on or off shows or hides its tools at once. It knows nothing of any channel.
/// </summary>
internal interface IToolSource
{
    /// <summary>The tools now; a name of the registry's fixed tools is never taken over.</summary>
    IReadOnlyList<OadmTool> Current();
}

/// <summary>A picture a tool answers besides its JSON (a camera snapshot): the bytes and their media type, whatever channel shows it.</summary>
/// <param name="Bytes">The encoded picture.</param>
/// <param name="MediaType">Its media type, e.g. <c>image/jpeg</c>.</param>
internal sealed record ToolImage(byte[] Bytes, string MediaType);

/// <summary>One call of a tool: the caller, the checked arguments and the way to the gRPC services.</summary>
internal sealed record ToolCall(ToolCaller Caller, JsonElement Arguments, InProcessCalls Calls, CancellationToken Ct)
{
    /// <summary>The name of the tool being called (cursors are bound to it).</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>A tool that answers a picture sets it here; it goes with the answer (<see cref="ToolResult.Image"/>).</summary>
    public ToolImage? Image { get; set; }

    /// <summary>The secret channel: a preview declares its slots here (<see cref="ToolSecretAnswers.Declare"/>); null without one.</summary>
    public ToolSecrets? SecretStore { get; init; }

    /// <summary>The secrets of the run's handle, taken by the registry before the call (in memory only, never in an answer).</summary>
    public IReadOnlyDictionary<string, string> SecretValues { get; init; } = new Dictionary<string, string>();

    /// <summary>The value of the secret <paramref name="name"/> the preview declared; null when the call has none.</summary>
    public string? Secret(string name) => SecretValues.TryGetValue(name, out var value) ? value : null;

    /// <summary>A run that starts tasks sets their ids here (<see cref="ToolResult.TaskIds"/>): the assistant's card follows them.</summary>
    public IReadOnlyList<string>? TaskIds { get; set; }

    /// <summary>
    /// A run that made a secret (a new access key) offers it here once (<see cref="ToolResult.SecretFile"/>): never in the JSON,
    /// so it never reaches a model; the assistant shows it as a card with Copy.
    /// </summary>
    public ToolSecretFile? SecretFile { get; set; }

    /// <summary>A gRPC method of the server as the caller's user.</summary>
    public Task<TResponse> CallAsync<TService, TRequest, TResponse>(string path, TRequest request, Func<TService, TRequest, ServerCallContext, Task<TResponse>> call)
        where TService : class =>
        Calls.CallAsync(Caller.User, path, request, call, Ct);

    public string? Text(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public int? Number(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    public bool? Flag(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public JsonElement? Element(string name) => Arguments.TryGetProperty(name, out var value) ? value : null;

    public IReadOnlyList<string> Texts(string name) =>
        Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];
}

/// <summary>A tool refuses the call: the caller gets this message (and field errors when there are).</summary>
internal sealed class ToolException(string message, IReadOnlyList<ToolFieldError>? fields = null) : Exception(message)
{
    public IReadOnlyList<ToolFieldError> Fields { get; } = fields ?? [];
}

/// <summary>The answer of one tool call: the JSON of the tool, or a refusal with its field errors.</summary>
internal sealed record ToolResult(JsonObject? Answer, string? Error, IReadOnlyList<ToolFieldError> Fields)
{
    public bool IsError => Error is not null;

    /// <summary>A picture that goes with <see cref="Answer"/> (at most <see cref="ToolAnswers.MaxImageBytes"/>); null for most tools.</summary>
    public ToolImage? Image { get; init; }

    /// <summary>The tasks a run started, in device order; null for most tools (never in the JSON: it grows with the devices).</summary>
    public IReadOnlyList<string>? TaskIds { get; init; }

    /// <summary>A secret the run made, offered once (never in the JSON); null for most tools.</summary>
    public ToolSecretFile? SecretFile { get; init; }

    public static ToolResult Ok(JsonObject answer) => new(answer, null, []);

    public static ToolResult Refused(string error, IReadOnlyList<ToolFieldError>? fields = null) => new(null, error, fields ?? []);
}

/// <summary>
/// The one tool registry of the server (docs/spec/mcp.md "Tools and MCP"): every tool with its descriptor, schemas, permission
/// and invoke method, independent of any channel. MCP is an adapter on top of it (transport, access keys, its own limits); a
/// later built-in assistant calls the same tools in process. The list shows only what the caller may do now (rule 5: the
/// role and device group of the methods each tool calls, the plugins that are on); a call of a tool
/// that is not listed answers the refusal; every argument is checked against the published schema first; every answer is at
/// most <see cref="ToolAnswers.MaxBytes"/>. Device texts are only ever JSON values (rule 6).
/// </summary>
internal sealed class ToolRegistry
{
    private readonly Dictionary<string, OadmTool> _tools;
    private readonly IReadOnlyList<IToolSource> _sources;
    private readonly InProcessCalls _calls;
    private readonly ToolSecrets? _secrets;

    public ToolRegistry(IEnumerable<OadmTool> tools, InProcessCalls calls, IEnumerable<IToolSource>? sources = null, ToolSecrets? secrets = null)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _sources = [.. sources ?? []];
        _calls = calls;
        _secrets = secrets;
    }

    /// <summary>The secret channel of the registry's tools (docs/spec/mcp.md "Secrets"); null without one.</summary>
    public ToolSecrets? Secrets => _secrets;

    /// <summary>The fixed tools and those of the sources now; a source never replaces a fixed tool or another source's first.</summary>
    private Dictionary<string, OadmTool> Now()
    {
        if (_sources.Count == 0)
        {
            return _tools;
        }

        var all = new Dictionary<string, OadmTool>(_tools, StringComparer.Ordinal);
        foreach (var tool in _sources.SelectMany(s => s.Current()))
        {
            all.TryAdd(tool.Name, tool);
        }

        return all;
    }

    /// <summary>Every tool, also those a caller does not see (tests).</summary>
    public IReadOnlyCollection<OadmTool> All => Now().Values.ToList();

    /// <summary>The tool of <paramref name="name"/>; false for a name no tool has.</summary>
    public bool TryGet(string? name, out OadmTool tool) => Now().TryGetValue(name ?? string.Empty, out tool!);

    /// <summary>The tools <paramref name="caller"/> may use now, in a fixed order.</summary>
    public IReadOnlyList<OadmTool> For(ToolCaller caller) =>
        [.. Now().Values.Where(t => Refusal(t, caller) is null).OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>
    /// Calls the tool <paramref name="name"/> (it must exist, <see cref="TryGet"/>) as <paramref name="caller"/>: the refusal
    /// when the caller may not use it, the field errors of the arguments, else the tool's answer or its refusal.
    /// </summary>
    public async Task<ToolResult> CallAsync(ToolCaller caller, string name, JsonElement? arguments, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!TryGet(name, out var tool))
        {
            throw new ArgumentException("No tool has this name.", nameof(name));
        }

        if (Refusal(tool, caller) is { } refusal)
        {
            return ToolResult.Refused(refusal);
        }

        var args = arguments is { ValueKind: JsonValueKind.Object } given ? given : EmptyObject;
        if (ToolSchema.Validate(tool.Input, args) is { Count: > 0 } errors)
        {
            return ToolResult.Refused(ToolAnswers.FieldsMessage, errors);
        }

        // A run whose preview declared secrets: refused while one is missing (the handle stays), else it gets them once.
        IReadOnlyDictionary<string, string>? secretValues = null;
        if (_secrets is not null && !tool.ReadsOnly && args.TryGetProperty("handle", out var handleArgument) && handleArgument.ValueKind == JsonValueKind.String)
        {
            var handle = handleArgument.GetString();
            if (_secrets.Missing(caller.BindingKey, handle, tool.Name) is { Count: > 0 } missing)
            {
                return ToolResult.Refused(ToolSecrets.Enter(missing[0]), [.. missing.Select(m => new ToolFieldError("secrets." + m.Name, ToolSecrets.Enter(m)))]);
            }

            secretValues = _secrets.Take(caller.BindingKey, handle, tool.Name);
        }

        try
        {
            args = SelectionTools.Apply(caller, args); // "selection": true is the caller's selected devices
            var call = new ToolCall(caller, args, _calls, ct) { Tool = tool.Name, SecretStore = _secrets, SecretValues = secretValues ?? new Dictionary<string, string>() };
            var answer = await tool.Run(call).ConfigureAwait(false);
            return ToolResult.Ok(answer) with { Image = call.Image, TaskIds = call.TaskIds, SecretFile = call.SecretFile };
        }
        catch (ToolException ex)
        {
            return ToolResult.Refused(ex.Message, ex.Fields);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            // What a client would read: "Your role does not allow: ...", "3 devices are no longer available." (never a stack trace).
            return ToolResult.Refused(string.IsNullOrWhiteSpace(ex.Status.Detail) ? ToolAnswers.Failed : ex.Status.Detail);
        }
    }

    /// <summary>Why <paramref name="caller"/> may not use <paramref name="tool"/> now; null when it may.</summary>
    public string? Refusal(OadmTool tool, ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(caller);
        return tool.Methods.FirstOrDefault(m => !InProcessCalls.Allows(m, caller.User)) is { } method
            ? _calls.DeniedMessage(method, caller.User)
            : tool.Gate?.Invoke(caller);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}

/// <summary>The answers of the tools: JSON objects of at most <see cref="MaxBytes"/>, and the shared refusal texts.</summary>
internal static class ToolAnswers
{
    /// <summary>The largest answer (about 8,000 tokens; docs/spec/mcp.md "Tests").</summary>
    public const int MaxBytes = 32 * 1024;

    /// <summary>The largest picture a tool answers besides its JSON (a camera snapshot).</summary>
    public const int MaxImageBytes = 1024 * 1024;

    /// <summary>Rows of a list by default and at most (rule 3).</summary>
    public const int DefaultRows = 50;
    public const int MaxRows = 200;

    /// <summary>The longest cursor (the last row's sort key and id, signed).</summary>
    public const int MaxCursorLength = 1024;

    public const string FieldsMessage = "Some arguments are not valid. Correct them and call again.";
    public const string Failed = "The call failed. Try again.";

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static int SizeOf(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(Json));

    /// <summary>A refusal as JSON: <c>{"error": "...", "errors": [{"field": "...", "error": "..."}]}</c> (at most 20 fields).</summary>
    public static JsonObject ErrorJson(string message, IReadOnlyList<ToolFieldError>? fields = null)
    {
        var answer = new JsonObject { ["error"] = message };
        if (fields is { Count: > 0 })
        {
            answer["errors"] = new JsonArray([.. fields.Take(20).Select(f => (JsonNode)new JsonObject { ["field"] = f.Field, ["error"] = f.Error })]);
        }

        return answer;
    }
}
