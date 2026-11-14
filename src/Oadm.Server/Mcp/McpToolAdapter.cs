using System.Text.Json;
using System.Text.Json.Nodes;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using Oadm.Server.Tools;

namespace Oadm.Server.Mcp;

/// <summary>
/// MCP on top of the tool registry (docs/spec/mcp.md "Tools and MCP"): the two MCP handlers <c>tools/list</c> and
/// <c>tools/call</c> as MCP types, nothing more. The tools, their schemas, permissions and answers are the registry's; a tool
/// gets the access key's caller and never a request. Every answer is structured content plus the same JSON as text; a
/// refusal is <c>isError</c> with <c>{"error": ..., "errors": [...]}</c>.
/// </summary>
internal sealed class McpToolAdapter(ToolRegistry tools)
{
    /// <summary>The registry the adapter serves.</summary>
    public ToolRegistry Tools => tools;

    /// <summary>The answer of <c>tools/list</c>.</summary>
    public ListToolsResult List(ToolCaller caller) => new() { Tools = [.. tools.For(caller).Select(ToProtocol)] };

    /// <summary>The answer of <c>tools/call</c>; an unknown name is a protocol error (-32602).</summary>
    public async Task<CallToolResult> CallAsync(ToolCaller caller, string name, JsonElement? arguments, CancellationToken ct)
    {
        if (!tools.TryGet(name, out _))
        {
            throw new McpProtocolException("Unknown tool.", McpErrorCode.InvalidParams);
        }

        var result = await tools.CallAsync(caller, name, arguments, ct).ConfigureAwait(false);
        if (result.IsError)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = ToolAnswers.ErrorJson(result.Error!, result.Fields).ToJsonString(ToolAnswers.Json) }],
                IsError = true,
            };
        }

        return Ok(result.Answer!);
    }

    /// <summary>Structured content and the same JSON as text.</summary>
    public static CallToolResult Ok(JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = answer.ToJsonString(ToolAnswers.Json) }],
            StructuredContent = JsonSerializer.SerializeToElement(answer, ToolAnswers.Json),
            IsError = false,
        };
    }

    private static Tool ToProtocol(OadmTool tool) => new()
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
