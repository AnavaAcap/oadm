using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.AspNetCore.Http;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using Oadm.Core.Auth;
using Oadm.Server.Auth;

namespace Oadm.Server.Mcp;

/// <summary>
/// One tool (docs/spec/mcp.md "Tools"): a user action, named <c>oadm_&lt;area&gt;_&lt;action&gt;</c>, with a title, a short
/// description, a strict input schema, an output schema and honest annotations. <see cref="Methods"/> are the gRPC methods it
/// calls: it is listed only for a caller who may call all of them, so the list follows the role, the device group and the
/// plugins of now.
/// </summary>
internal sealed record McpTool(
    string Name,
    string Title,
    string Description,
    JsonObject Input,
    JsonObject Output,
    IReadOnlyList<string> Methods,
    Func<McpToolCall, Task<JsonObject>> Run)
{
    /// <summary>Reads only: <c>readOnlyHint</c>. Stage 1 has only such tools; changes come as preview and run (stage 2).</summary>
    public bool ReadsOnly { get; init; } = true;

    public bool Destructive { get; init; }

    public bool Idempotent { get; init; } = true;
}

/// <summary>One call of a tool: the caller, the checked arguments and the way to the gRPC services.</summary>
internal sealed record McpToolCall(McpCaller Caller, JsonElement Arguments, InProcessCalls Calls, HttpContext? Http, CancellationToken Ct)
{
    /// <summary>A gRPC method of the server as the key's user.</summary>
    public Task<TResponse> CallAsync<TService, TRequest, TResponse>(string path, TRequest request, Func<TService, TRequest, ServerCallContext, Task<TResponse>> call)
        where TService : class =>
        Calls.CallAsync(Caller.Caller, path, request, call, Http, Ct);

    public string? Text(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public int? Number(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    public bool? Flag(string name) => Arguments.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    public JsonElement? Element(string name) => Arguments.TryGetProperty(name, out var value) ? value : null;

    public IReadOnlyList<string> Texts(string name) =>
        Arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];
}

/// <summary>A tool refuses the call: the agent gets <c>isError</c> with this message (and field errors when there are).</summary>
internal sealed class McpToolException(string message, IReadOnlyList<McpFieldError>? fields = null) : Exception(message)
{
    public IReadOnlyList<McpFieldError> Fields { get; } = fields ?? [];
}

/// <summary>
/// The tools of the MCP server and the two MCP handlers (<c>tools/list</c>, <c>tools/call</c>): the list shows only what the
/// key's user may do now (rule 5: "Use AI agents", the role and device group of the methods each tool calls, Read only
/// keys only reading tools); a call of a tool that is not listed answers <c>isError</c> with
/// <see cref="AccessPolicy.DeniedMessage(AccessRule, PermissionCatalog, AuthenticatedCaller)"/>; every argument is checked
/// against the published schema first; every answer is at most <see cref="McpAnswers.MaxBytes"/> and comes as structured
/// content plus the same JSON as text. Device texts are only ever JSON values (rule 6).
/// </summary>
internal sealed class McpTools
{
    private readonly Dictionary<string, McpTool> _tools;
    private readonly InProcessCalls _calls;
    private readonly RoleStore _roles;

    public McpTools(IEnumerable<McpTool> tools, InProcessCalls calls, RoleStore roles)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _calls = calls;
        _roles = roles;
    }

    /// <summary>Every tool, also those a caller does not see (tests).</summary>
    public IReadOnlyCollection<McpTool> All => _tools.Values;

    /// <summary>The tools <paramref name="caller"/> may use now, in a fixed order.</summary>
    public IReadOnlyList<McpTool> For(McpCaller caller) =>
        [.. _tools.Values.Where(t => Refusal(t, caller) is null).OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>The answer of <c>tools/list</c>.</summary>
    public ListToolsResult List(McpCaller caller) => new() { Tools = [.. For(caller).Select(ToProtocol)] };

    /// <summary>The answer of <c>tools/call</c>.</summary>
    public async Task<CallToolResult> CallAsync(McpCaller caller, string name, JsonElement? arguments, HttpContext? http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (!_tools.TryGetValue(name ?? string.Empty, out var tool))
        {
            throw new McpProtocolException("Unknown tool.", McpErrorCode.InvalidParams);
        }

        if (Refusal(tool, caller) is { } refusal)
        {
            return McpAnswers.Error(refusal);
        }

        var args = arguments is { ValueKind: JsonValueKind.Object } given ? given : EmptyObject;
        if (McpSchema.Validate(tool.Input, args) is { Count: > 0 } errors)
        {
            return McpAnswers.Error(McpAnswers.FieldsMessage, errors);
        }

        try
        {
            var answer = await tool.Run(new McpToolCall(caller, args, _calls, http, ct)).ConfigureAwait(false);
            return McpAnswers.Ok(answer);
        }
        catch (McpToolException ex)
        {
            return McpAnswers.Error(ex.Message, ex.Fields);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            // What a client would read: "Your role does not allow: ...", "3 devices are no longer available." (never a stack trace).
            return McpAnswers.Error(string.IsNullOrWhiteSpace(ex.Status.Detail) ? McpAnswers.Failed : ex.Status.Detail);
        }
    }

    /// <summary>Why <paramref name="caller"/> may not use <paramref name="tool"/> now; null when it may.</summary>
    private string? Refusal(McpTool tool, McpCaller caller)
    {
        if (!caller.Caller.Has(HostPermissions.McpUse))
        {
            return _roles.Catalog.DeniedMessage(HostPermissions.McpUse);
        }

        if (caller.ReadOnly && !tool.ReadsOnly)
        {
            return McpAnswers.ReadOnlyKey;
        }

        return tool.Methods.FirstOrDefault(m => !InProcessCalls.Allows(m, caller.Caller)) is { } method
            ? _calls.DeniedMessage(method, caller.Caller)
            : null;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static Tool ToProtocol(McpTool tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = JsonSerializer.SerializeToElement(tool.Input),
        OutputSchema = JsonSerializer.SerializeToElement(tool.Output),
        Annotations = new ToolAnnotations
        {
            Title = tool.Title,
            ReadOnlyHint = tool.ReadsOnly,
            DestructiveHint = tool.Destructive,
            IdempotentHint = tool.Idempotent,
            OpenWorldHint = false, // OADM's own devices and data
        },
    };
}

/// <summary>The answers of the tools: structured content and the same JSON as text, at most <see cref="MaxBytes"/>.</summary>
internal static class McpAnswers
{
    /// <summary>The largest answer (about 8,000 tokens; docs/spec/mcp.md "Tests").</summary>
    public const int MaxBytes = 32 * 1024;

    /// <summary>Rows of a list by default and at most (rule 3).</summary>
    public const int DefaultRows = 50;
    public const int MaxRows = 200;

    public const string FieldsMessage = "Some arguments are not valid. Correct them and call again.";
    public const string Failed = "The call failed. Try again.";
    public const string ReadOnlyKey = "This access key is Read only.";

    public static readonly JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static int SizeOf(JsonNode node) => Encoding.UTF8.GetByteCount(node.ToJsonString(Json));

    public static CallToolResult Ok(JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var text = answer.ToJsonString(Json);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }],
            StructuredContent = JsonSerializer.SerializeToElement(answer, Json),
            IsError = false,
        };
    }

    /// <summary>A refusal: <c>{"error": "...", "errors": [{"field": "...", "error": "..."}]}</c> as text, <c>isError</c>.</summary>
    public static CallToolResult Error(string message, IReadOnlyList<McpFieldError>? fields = null)
    {
        var answer = new JsonObject { ["error"] = message };
        if (fields is { Count: > 0 })
        {
            answer["errors"] = new JsonArray([.. fields.Take(20).Select(f => (JsonNode)new JsonObject { ["field"] = f.Field, ["error"] = f.Error })]);
        }

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = answer.ToJsonString(Json) }],
            IsError = true,
        };
    }
}
