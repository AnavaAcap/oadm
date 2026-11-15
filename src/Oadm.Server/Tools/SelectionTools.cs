using System.Text.Json;
using System.Text.Json.Nodes;

using static Oadm.Server.Tools.ToolSchema;

namespace Oadm.Server.Tools;

/// <summary>
/// The devices the user selected in the client's device grid (docs/spec/assistant.md "What the user gives with a message"):
/// the assistant sends them with every message, already cut to the caller's device view (<see cref="ToolCaller.Selection"/>).
/// <c>oadm_selection</c> pages them like <c>oadm_devices_search</c>, and <c>"selection": true</c> in the device filter of every
/// device tool means "these devices". Over MCP nothing is selected: the tool answers an empty list and the filter is refused.
/// </summary>
internal static class SelectionTools
{
    public const string Name = "oadm_selection";

    public const string NothingSelected = "No devices are selected. Select them in the device list first.";
    public const string NoneOfThese = "None of these devices is selected.";

    /// <summary>The filter property of the device filter (<see cref="DeviceTools.Filter"/>).</summary>
    public const string FilterProperty = "selection";

    public static JsonObject FilterSchema() =>
        Flag("Only the devices the user selected in the device list of OADM (the assistant's chat; over MCP none are).");

    public static IEnumerable<OadmTool> Create(ToolCursors cursors)
    {
        yield return new OadmTool(
            Name,
            "Selected devices",
            "The devices the user selected in the device list, a page at a time with the total and a cursor. Over MCP none are selected.",
            Object(null,
            [
                P("fields", List("Fields to add to each row.", Choice("A field.", DeviceTools.ExtraFields), DeviceTools.ExtraFields.Count)),
                P("cursor", Text("next_cursor of the previous page.", ToolAnswers.MaxCursorLength)),
                P("limit", Number("Rows per page.", 1, ToolAnswers.MaxRows, ToolAnswers.DefaultRows)),
            ]),
            DeviceTools.SearchOutput(),
            [DeviceTools.SearchPath],
            call => SelectionAsync(call, cursors));
    }

    private static Task<JsonObject> SelectionAsync(ToolCall call, ToolCursors cursors)
    {
        var selection = call.Caller.Selection ?? [];
        if (selection.Count == 0)
        {
            return Task.FromResult(new JsonObject
            {
                ["total"] = 0,
                ["devices"] = new JsonArray(),
                ["next_cursor"] = null,
                ["note"] = NothingSelected,
            });
        }

        // The search of these ids: the caller's view, the address order and the cursors of the device search.
        var arguments = JsonNode.Parse(call.Arguments.GetRawText())!.AsObject();
        arguments["filter"] = new JsonObject { ["ids"] = new JsonArray([.. selection.Select(id => (JsonNode)JsonValue.Create(id)!)]) };
        return DeviceTools.SearchPageAsync(call with { Arguments = JsonSerializer.SerializeToElement(arguments) }, cursors);
    }

    /// <summary>
    /// The arguments with <c>filter.selection</c> applied: <c>true</c> becomes the selected ids (and with <c>ids</c> as well, the
    /// ids that are both), <c>false</c> is left out. A refusal when nothing is selected, so an empty selection never means every
    /// device. Called by the registry after the schema check and before the tool runs.
    /// </summary>
    public static JsonElement Apply(ToolCaller caller, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("filter", out var filter)
            || filter.ValueKind != JsonValueKind.Object || !filter.TryGetProperty(FilterProperty, out var flag))
        {
            return arguments;
        }

        var rewritten = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        var newFilter = rewritten["filter"]!.AsObject();
        newFilter.Remove(FilterProperty);
        if (flag.ValueKind == JsonValueKind.True)
        {
            var selection = caller.Selection ?? [];
            if (selection.Count == 0)
            {
                throw new ToolException(NothingSelected, [new ToolFieldError("filter.selection", NothingSelected)]);
            }

            IEnumerable<string> ids = selection;
            if (newFilter["ids"] is JsonArray given && given.Count > 0)
            {
                var wanted = given.Select(v => v?.GetValue<string>()?.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
                ids = [.. selection.Where(id => wanted.Contains(id.ToLowerInvariant()))];
                if (!ids.Any())
                {
                    throw new ToolException(NoneOfThese, [new ToolFieldError("filter.ids", NoneOfThese)]);
                }
            }

            newFilter["ids"] = new JsonArray([.. ids.Select(id => (JsonNode)JsonValue.Create(id)!)]);
        }

        return JsonSerializer.SerializeToElement(rewritten);
    }
}
