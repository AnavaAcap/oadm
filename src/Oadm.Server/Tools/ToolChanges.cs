using System.Text.Json.Nodes;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tools;

/// <summary>
/// The shared parts of a change's two tools (docs/spec/mcp.md rule 2): the run's input (only the handle), taking the handle
/// once for its own run tool, and the handle and its end in a preview's answer.
/// </summary>
internal static class ToolChanges
{
    /// <summary>A device id the caller does not see (unknown or outside the device group: the same answer).</summary>
    public const string UnknownDevice = "No device you see has this id.";

    /// <summary>The input of a run tool: the handle of <paramref name="preview"/>.</summary>
    public static JsonObject RunInput(string preview) =>
        Object(null, [P("handle", Text("The handle of " + preview + " (once, 10 minutes).", 64, minLength: 1))], "handle");

    /// <summary>The output properties every preview of these tools answers last.</summary>
    public static IEnumerable<KeyValuePair<string, JsonObject>> HandleOutput(string run) =>
    [
        P("warnings", OutList("What the user must know before the run.", Out("string", "A warning."))),
        P("handle", OrNull(Out("string", "Pass it to " + run + "; null when the run would do nothing."))),
        P("expires", OrNull(Out("string", "When the handle ends (UTC)."))),
    ];

    /// <summary>The plan of the run tool's handle, taken; the refusal "call the preview again" otherwise.</summary>
    public static T Take<T>(ToolCall call, ToolHandles handles)
        where T : class =>
        handles.TakeChange<T>(call.Caller.BindingKey, call.Text("handle"), call.Tool)
            ?? throw new ToolException(AreaListing.PreviewEnded, [new ToolFieldError("handle", AreaListing.PreviewEnded)]);

    /// <summary>
    /// Adds the handle of <paramref name="plan"/> for <paramref name="run"/> (none when <paramref name="plan"/> is null) and the
    /// warnings to <paramref name="answer"/>.
    /// </summary>
    public static JsonObject WithHandle(JsonObject answer, ToolCall call, ToolHandles handles, string run, object? plan, IEnumerable<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var handle = plan is null ? null : handles.AddChange(call.Caller.BindingKey, new PlannedChange(run, plan));
        answer["warnings"] = new JsonArray([.. (warnings ?? []).Select(w => (JsonNode)JsonValue.Create(w)!)]);
        answer["handle"] = handle;
        answer["expires"] = handle is null ? null : AreaListing.Time(handles.EndOfNew());
        return answer;
    }

    /// <summary>The names of <paramref name="extra"/> plus the three of <see cref="HandleOutput"/>, for <c>required</c>.</summary>
    public static string[] Required(params string[] extra) => [.. extra, "warnings", "handle", "expires"];
}
