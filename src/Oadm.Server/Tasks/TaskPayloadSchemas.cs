using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Server.Tasks;

/// <summary>
/// The JSON Schemas of task payloads (docs/spec/mcp.md "SDK"): <see cref="ITaskPlugin.PayloadType"/> exported with
/// <see cref="JsonSchemaExporter"/> and the plugin's own serializer options, so the schema names what the plugin reads.
/// Objects take no other properties; <c>[Description]</c>, <c>[MaxLength]</c>, <c>[MinLength]</c>, <c>[Length]</c>,
/// <c>[StringLength]</c> and <c>[Range]</c> become the schema's words; a text, number or list without its own bound gets the
/// host's (<see cref="DefaultMaxLength"/>, the 32-bit range, <see cref="DeviceLimits.MaxDevices"/> items). A
/// <see cref="SecretAttribute"/> property is left out wherever it sits (an agent never sends a secret; the person enters it,
/// <see cref="ITaskPlugin.SecretsOf"/>). Fail closed: no payload type, a value of any type, or a type the exporter cannot
/// describe means no schema, and the task is not offered to the tools.
/// </summary>
public static class TaskPayloadSchemas
{
    /// <summary>The longest text of a payload field without a bound of its own.</summary>
    public const int DefaultMaxLength = 4096;

    private static readonly ConditionalWeakTable<Type, Holder> Cache = []; // a plugin's types go with the plugin

    /// <summary>The schema of the plugin's payload; null when the task is not offered to the tools.</summary>
    public static JsonObject? Of(ITaskPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        Type? type;
        JsonSerializerOptions? options;
        try
        {
            type = plugin.PayloadType;
            options = plugin.PayloadJsonOptions;
        }
#pragma warning disable CA1031 // A plugin with a throwing property is not offered; the menu keeps working.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }

        return type is null ? null : Cache.GetValue(type, t => new Holder(Build(t, options))).Schema?.DeepClone() as JsonObject;
    }

    /// <summary>The schema of <paramref name="type"/> read with <paramref name="options"/>; null when it cannot be offered.</summary>
    public static JsonObject? Build(Type type, JsonSerializerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(type);
        var export = new JsonSerializerOptions(options ?? PluginJson.CreateOptions()) { RespectRequiredConstructorParameters = true };
        export.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
        JsonNode schema;
        try
        {
            schema = export.GetJsonSchemaAsNode(type, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform });
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }

        if (schema is JsonObject top)
        {
            WithoutSecrets(top);
        }

        return schema is JsonObject root && TypesOf(root).Contains("object") && Describable(root) ? root : null;
    }

    /// <summary>The mark <see cref="Transform"/> puts on a secret property, taken out with the property by <see cref="WithoutSecrets"/>.</summary>
    private const string SecretMark = "x-oadm-secret";

    /// <summary>True when <paramref name="type"/> has a <see cref="SecretAttribute"/> property anywhere (it is left out of the schema).</summary>
    public static bool HasSecrets(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return HasSecret(type, []);
    }

    /// <summary>Removes every property marked secret (and its name from <c>required</c>), at every level.</summary>
    private static void WithoutSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject schema:
                if (schema["properties"] is JsonObject properties)
                {
                    foreach (var name in properties.Where(p => p.Value is JsonObject value && value.ContainsKey(SecretMark)).Select(p => p.Key).ToList())
                    {
                        properties.Remove(name);
                        if (schema["required"] is JsonArray required && required.FirstOrDefault(r => r?.GetValue<string>() == name) is { } entry)
                        {
                            required.Remove(entry);
                        }
                    }
                }

                foreach (var child in schema.Select(p => p.Value).ToList())
                {
                    WithoutSecrets(child);
                }

                break;
            case JsonArray list:
                foreach (var child in list.ToList())
                {
                    WithoutSecrets(child);
                }

                break;
        }
    }

    /// <summary>A secret property anywhere in the payload (its properties, their element types, their properties).</summary>
    private static bool HasSecret(Type type, HashSet<Type> seen)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum || !seen.Add(type))
        {
            return false;
        }

        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            return HasSecret(inner, seen);
        }

        if (type.IsArray)
        {
            return HasSecret(type.GetElementType()!, seen);
        }

        if (type.IsGenericType && type.GetGenericArguments().Any(a => HasSecret(a, seen)))
        {
            return true;
        }

        if (type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
        {
            return false;
        }

        var parameters = type.GetConstructors().SelectMany(c => c.GetParameters()).ToList();
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p =>
            p.GetCustomAttribute<SecretAttribute>() is not null
            || parameters.Any(c => string.Equals(c.Name, p.Name, StringComparison.OrdinalIgnoreCase) && c.GetCustomAttribute<SecretAttribute>() is not null)
            || HasSecret(p.PropertyType, seen));
    }

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema)
        {
            return node; // true: a value of any type (Describable refuses it)
        }

        var attributes = new List<Attribute>();
        if (context.PropertyInfo?.AttributeProvider is { } property)
        {
            attributes.AddRange(property.GetCustomAttributes(true).OfType<Attribute>());
        }

        if (context.PropertyInfo?.AssociatedParameter?.AttributeProvider is { } parameter)
        {
            attributes.AddRange(parameter.GetCustomAttributes(true).OfType<Attribute>());
        }

        if (attributes.OfType<SecretAttribute>().Any())
        {
            return new JsonObject { [SecretMark] = true }; // left out by WithoutSecrets: an agent never sends a secret
        }

        if (context.PropertyInfo is null && context.TypeInfo.Type.GetCustomAttribute<DescriptionAttribute>() is { } typeDescription)
        {
            attributes.Add(typeDescription);
        }

        var types = TypesOf(schema);
        if (types.Contains("string") && (types.Contains("integer") || types.Contains("number")))
        {
            // Numbers a plugin also reads from text (JsonSerializerDefaults.Web): the tools take the number only.
            schema["type"] = new JsonArray([.. types.Where(t => t != "string").Select(t => (JsonNode)JsonValue.Create(t)!)]);
            schema.Remove("pattern");
            types = TypesOf(schema);
        }

        foreach (var attribute in attributes)
        {
            switch (attribute)
            {
                case DescriptionAttribute description when schema["description"] is null:
                    schema["description"] = description.Description;
                    break;
                case MaxLengthAttribute max:
                    schema[types.Contains("array") ? "maxItems" : "maxLength"] = max.Length;
                    break;
                case MinLengthAttribute min:
                    schema[types.Contains("array") ? "minItems" : "minLength"] = min.Length;
                    break;
                case LengthAttribute length:
                    schema[types.Contains("array") ? "minItems" : "minLength"] = length.MinimumLength;
                    schema[types.Contains("array") ? "maxItems" : "maxLength"] = length.MaximumLength;
                    break;
                case StringLengthAttribute stringLength:
                    schema["maxLength"] = stringLength.MaximumLength;
                    if (stringLength.MinimumLength > 0)
                    {
                        schema["minLength"] = stringLength.MinimumLength;
                    }

                    break;
                case RangeAttribute range:
                    schema["minimum"] = JsonValue.Create(Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture));
                    schema["maximum"] = JsonValue.Create(Convert.ToDouble(range.Maximum, System.Globalization.CultureInfo.InvariantCulture));
                    break;
            }
        }

        if (types.Contains("object"))
        {
            if (schema["additionalProperties"] is JsonObject)
            {
                // A map (devices by id): its values have their schema already; it has a size like a list, its names an id format.
                schema["maxProperties"] = DeviceLimits.MaxDevices;
                if (KeyTypeOf(context.TypeInfo.Type) == typeof(Guid))
                {
                    schema["propertyNames"] = new JsonObject { ["type"] = "string", ["format"] = "uuid" };
                }
            }
            else
            {
                schema["additionalProperties"] = false;
            }
        }

        if (types.Contains("string") && schema["enum"] is null && schema["maxLength"] is null)
        {
            schema["maxLength"] = DefaultMaxLength;
        }

        if (types.Contains("integer") && schema["minimum"] is null)
        {
            schema["minimum"] = int.MinValue;
            schema["maximum"] = int.MaxValue;
        }

        if (types.Contains("array") && schema["maxItems"] is null)
        {
            schema["maxItems"] = DeviceLimits.MaxDevices;
        }

        return schema;
    }

    /// <summary>The key type of a dictionary type; null for any other type.</summary>
    private static Type? KeyTypeOf(Type type) =>
        type.GetInterfaces().Prepend(type)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() is var definition
                && (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)))
            ?.GetGenericArguments()[0];

    /// <summary>Every node says what it takes: a type or a list of choices, no value of any type, no reference.</summary>
    private static bool Describable(JsonNode? node) => node switch
    {
        JsonObject schema when schema["$ref"] is not null => false,
        JsonObject schema => (TypesOf(schema).Count > 0 || schema["enum"] is JsonArray)
            && (schema["properties"] is not JsonObject properties || properties.All(p => Describable(p.Value)))
            && (schema["items"] is null || Describable(schema["items"]))
            && (schema["additionalProperties"] is not JsonObject entries || Describable(entries)),
        _ => false,
    };

    /// <summary>The types of a schema node ("string", or ["string", "null"]).</summary>
    public static IReadOnlyList<string> TypesOf(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema["type"] switch
        {
            JsonValue value when value.TryGetValue<string>(out var single) => [single],
            JsonArray many => [.. many.Select(t => t?.GetValue<string>() ?? string.Empty)],
            _ => [],
        };
    }

    private sealed class Holder(JsonObject? schema)
    {
        public JsonObject? Schema { get; } = schema;
    }
}
