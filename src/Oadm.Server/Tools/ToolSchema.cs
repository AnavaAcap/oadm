using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Server.Tools;

/// <summary>
/// The JSON Schemas of the tools (JSON Schema 2020-12, the default of MCP): built here, published as the tools' input and output
/// schemas, and the same input schema checks every call (<see cref="Validate"/>), so what an agent reads is what the server
/// enforces. Objects never take other properties (<c>additionalProperties: false</c>); strings have a length, numbers a range,
/// lists a size, choices an <c>enum</c>.
/// </summary>
internal static class ToolSchema
{
    public static JsonObject Text(string description, int maxLength, string? format = null, int minLength = 0)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description, ["maxLength"] = maxLength };
        if (minLength > 0)
        {
            schema["minLength"] = minLength;
        }

        if (format is not null)
        {
            schema["format"] = format;
        }

        return schema;
    }

    public static JsonObject Choice(string description, IEnumerable<string> values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = Array(values) };

    public static JsonObject Number(string description, int minimum, int maximum, int? defaultValue = null)
    {
        var schema = new JsonObject { ["type"] = "integer", ["description"] = description, ["minimum"] = minimum, ["maximum"] = maximum };
        if (defaultValue is { } value)
        {
            schema["default"] = value;
        }

        return schema;
    }

    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject List(string description, JsonObject items, int maxItems) =>
        new() { ["type"] = "array", ["description"] = description, ["items"] = items, ["maxItems"] = maxItems };

    /// <summary>An object that takes only <paramref name="properties"/>.</summary>
    public static JsonObject Object(string? description, IEnumerable<KeyValuePair<string, JsonObject>> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, property) in properties)
        {
            props[name] = property;
        }

        var result = new JsonObject { ["type"] = "object" };
        if (description is not null)
        {
            result["description"] = description;
        }

        result["properties"] = props;
        if (required.Length > 0)
        {
            result["required"] = Array(required);
        }

        result["additionalProperties"] = false;
        return result;
    }

    public static KeyValuePair<string, JsonObject> P(string name, JsonObject schema) => new(name, schema);

    /// <summary>A value of an output schema that may be null (a time not reached yet, a value the device did not report).</summary>
    public static JsonObject OrNull(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var type = schema["type"]?.GetValue<string>() ?? "string";
        schema["type"] = Array([type, "null"]);
        return schema;
    }

    /// <summary>An output value: a type and a description (answers are checked by tests, not by the server).</summary>
    public static JsonObject Out(string type, string description) => new() { ["type"] = type, ["description"] = description };

    public static JsonObject OutList(string description, JsonObject items) => new() { ["type"] = "array", ["description"] = description, ["items"] = items };

    /// <summary>An output object (its known properties; <c>required</c> lists what every answer carries).</summary>
    public static JsonObject OutObject(string? description, IEnumerable<KeyValuePair<string, JsonObject>> properties, params string[] required)
    {
        var schema = Object(description, properties, required);
        return schema;
    }

    private static JsonArray Array(IEnumerable<string> values) => [.. values.Select(v => (JsonNode)JsonValue.Create(v)!)];

    /// <summary>
    /// The errors of <paramref name="value"/> against <paramref name="schema"/> (the subset these schemas use), one per field:
    /// ("filter.tags[2]", "Use at most 64 characters."). Empty when it fits.
    /// </summary>
    public static IReadOnlyList<ToolFieldError> Validate(JsonObject schema, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var errors = new List<ToolFieldError>();
        Check(schema, value, string.Empty, errors);
        return errors;
    }

    private static void Check(JsonObject schema, JsonElement value, string path, List<ToolFieldError> errors)
    {
        var type = schema["type"]?.GetValue<string>();
        var field = path.Length == 0 ? "arguments" : path;
        switch (type)
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new(field, ToolInputTexts.Object));
                    return;
                }

                var properties = schema["properties"] as JsonObject ?? [];
                foreach (var property in value.EnumerateObject())
                {
                    if (properties[property.Name] is JsonObject inner)
                    {
                        Check(inner, property.Value, Join(path, property.Name), errors);
                    }
                    else
                    {
                        errors.Add(new(Join(path, property.Name), ToolInputTexts.Unknown));
                    }
                }

                foreach (var name in (schema["required"] as JsonArray ?? []).Select(n => n!.GetValue<string>()))
                {
                    if (!value.TryGetProperty(name, out _))
                    {
                        errors.Add(new(Join(path, name), ToolInputTexts.Required));
                    }
                }

                return;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add(new(field, ToolInputTexts.List));
                    return;
                }

                var max = schema["maxItems"]?.GetValue<int>() ?? int.MaxValue;
                if (value.GetArrayLength() > max)
                {
                    errors.Add(new(field, string.Create(CultureInfo.InvariantCulture, $"Use at most {max:N0} values.")));
                    return;
                }

                var items = schema["items"] as JsonObject ?? [];
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Check(items, item, string.Create(CultureInfo.InvariantCulture, $"{field}[{index++}]"), errors);
                }

                return;
            case "string":
                CheckText(schema, value, field, errors);
                return;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
                {
                    errors.Add(new(field, ToolInputTexts.WholeNumber(schema)));
                    return;
                }

                if (number < (schema["minimum"]?.GetValue<int>() ?? int.MinValue) || number > (schema["maximum"]?.GetValue<int>() ?? int.MaxValue))
                {
                    errors.Add(new(field, ToolInputTexts.WholeNumber(schema)));
                }

                return;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add(new(field, ToolInputTexts.TrueOrFalse));
                }

                return;
        }
    }

    private static void CheckText(JsonObject schema, JsonElement value, string field, List<ToolFieldError> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add(new(field, ToolInputTexts.Text));
            return;
        }

        var text = value.GetString() ?? string.Empty;
        if (schema["enum"] is JsonArray choices)
        {
            var allowed = choices.Select(c => c!.GetValue<string>()).ToList();
            if (!allowed.Contains(text, StringComparer.Ordinal))
            {
                errors.Add(new(field, "Use one of: " + string.Join(", ", allowed) + "."));
            }

            return;
        }

        var max = schema["maxLength"]?.GetValue<int>() ?? int.MaxValue;
        var min = schema["minLength"]?.GetValue<int>() ?? 0;
        if (text.Length > max)
        {
            errors.Add(new(field, string.Create(CultureInfo.InvariantCulture, $"Use at most {max:N0} characters.")));
        }
        else if (text.Length < min)
        {
            errors.Add(new(field, ToolInputTexts.Required));
        }
        else if (text.Any(char.IsControl))
        {
            errors.Add(new(field, ToolInputTexts.ControlCharacters));
        }
        else if (schema["format"]?.GetValue<string>() is { } format && !Fits(format, text))
        {
            errors.Add(new(field, format switch
            {
                "uuid" => ToolInputTexts.Id,
                "date-time" => ToolInputTexts.Time,
                _ => ToolInputTexts.Text,
            }));
        }
    }

    private static bool Fits(string format, string text) => format switch
    {
        "uuid" => Guid.TryParseExact(text, "D", out _),
        "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && text.Contains('T', StringComparison.Ordinal),
        _ => true,
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
}

/// <summary>One refused field of a call: the path of the field and what to do.</summary>
internal sealed record ToolFieldError(string Field, string Error);

/// <summary>The texts of refused input (short, plain: what to enter instead).</summary>
internal static class ToolInputTexts
{
    public const string Object = "Pass an object.";
    public const string Unknown = "This field is not known. Leave it out.";
    public const string Required = "This field is required.";
    public const string List = "Pass a list.";
    public const string Text = "Enter text.";
    public const string TrueOrFalse = "Enter true or false.";
    public const string ControlCharacters = "Line breaks and other control characters are not allowed.";
    public const string Id = "Enter an id like 3f2b9c1e-6a4d-4e8f-9b2a-1c7d5e3f8a90.";
    public const string Time = "Enter a time like 2026-11-14T09:30:00Z.";

    public static string WholeNumber(JsonObject schema) =>
        string.Create(CultureInfo.InvariantCulture, $"Enter a whole number from {schema["minimum"]?.GetValue<int>() ?? 0:N0} to {schema["maximum"]?.GetValue<int>() ?? 0:N0}.");
}
