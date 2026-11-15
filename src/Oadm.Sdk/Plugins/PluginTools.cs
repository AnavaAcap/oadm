using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// One tool of a core plugin (SDK 0.13.0, <see cref="ICorePlugin.Tools"/>): a step a program, such as an assistant, may take
/// in the plugin without its page, described once so that a caller can read what it takes and what it answers. The tool
/// knows nothing of the channel that calls it; the host lists it only while the plugin is on and the caller's role has its
/// permission, checks the arguments against the input schema, and calls the plugin's own <see cref="ICorePlugin.InvokeAsync(ICallContext, string, string?, CancellationToken)"/>
/// with the caller (the methods <see cref="Method"/>, <see cref="PreviewMethod"/> and <see cref="RunMethod"/>), so every rule
/// of the plugin's page applies.
/// </summary>
/// <param name="Name">What the tool does, in lower case words with underscores ("certificates", "certificate_create"); the host adds the plugin's area in front.</param>
/// <param name="Title">The name for a person ("Certificate list").</param>
/// <param name="Description">One to three short sentences: what the tool does and when to call it. Never a text of a device or a user.</param>
public sealed record PluginTool(string Name, string Title, string Description)
{
    /// <summary>The longest name.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Rows of a list by default (<see cref="Paged"/>).</summary>
    public const int DefaultRows = 50;

    /// <summary>Rows of a list at most (<see cref="Paged"/>).</summary>
    public const int MaxRows = 200;

    /// <summary>
    /// The record of the arguments. The host exports its JSON Schema with the plugin's <see cref="JsonOptions"/> (descriptions and
    /// validation attributes become the schema's words; objects take no other properties; text, number and list have a bound).
    /// Null together with <see cref="InputSchema"/>: the tool takes no argument. A type with a secret property is never offered.
    /// </summary>
    public Type? InputType { get; init; }

    /// <summary>The input as JSON Schema text instead of <see cref="InputType"/>: an object with <c>additionalProperties: false</c>.</summary>
    public string? InputSchema { get; init; }

    /// <summary>
    /// The record of the answer (a JSON object) of <see cref="Method"/> for a read, of <see cref="PreviewMethod"/> for a change (the
    /// plan the host adds <c>handle</c> and <c>expires</c> to, so the type has no property of these names).
    /// </summary>
    public Type? OutputType { get; init; }

    /// <summary>The answer as JSON Schema text instead of <see cref="OutputType"/>.</summary>
    public string? OutputSchema { get; init; }

    /// <summary>The record of the answer of <see cref="RunMethod"/> (a change).</summary>
    public Type? RunOutputType { get; init; }

    /// <summary>The answer of <see cref="RunMethod"/> as JSON Schema text instead of <see cref="RunOutputType"/>.</summary>
    public string? RunOutputSchema { get; init; }

    /// <summary>The serializer options of the input and output types (their names and enum texts); null: <see cref="PluginJson.CreateOptions"/>.</summary>
    public JsonSerializerOptions? JsonOptions { get; init; }

    /// <summary>
    /// The tool changes something: the host makes two tools of it, <c>&lt;name&gt;_preview</c> (calls <see cref="PreviewMethod"/>,
    /// which checks everything and never writes) and <c>&lt;name&gt;_run</c> (takes only the handle of a preview and calls
    /// <see cref="RunMethod"/>, which checks everything again).
    /// </summary>
    public bool Changes { get; init; }

    /// <summary>A change that removes something or cannot be taken back (an honest hint for the caller).</summary>
    public bool Destructive { get; init; }

    /// <summary>Calling again with the same arguments changes nothing more; default true for a read, false for a change.</summary>
    public bool? Idempotent { get; init; }

    /// <summary>
    /// A right of the plugin (a key of <see cref="ICorePlugin.Permissions"/>, or <see cref="PermissionKeys.Show"/>) the caller needs
    /// on top of what the plugin asks for the method (<see cref="ICorePlugin.RequiredRole"/>, <see cref="ICorePlugin.RequiredPermission"/>);
    /// null: the method's own rule is the permission.
    /// </summary>
    public string? Permission { get; init; }

    /// <summary>
    /// A list: the host adds <c>limit</c> (1 to <see cref="MaxRows"/>, default <see cref="DefaultRows"/>) and <c>cursor</c> to the input,
    /// reads the cursor and hands the plugin <c>limit</c> and <c>after</c> (the sort key of the last row of the page before, see
    /// <see cref="PluginToolPage"/>); the answer's <c>next</c> (the sort key of the last row, null at the end) becomes <c>next_cursor</c>.
    /// The output type has <c>total</c> and <c>next</c>.
    /// </summary>
    public bool Paged { get; init; }

    /// <summary>Devices a tool with <see cref="DeviceFilter"/> gets at most (the first by address; <see cref="PluginToolDevices.Total"/> says how many matched).</summary>
    public const int MaxDevices = 500;

    /// <summary>
    /// The tool works on devices the caller chooses: the host adds <c>filter</c> (the device filter of the host's device tools,
    /// with the assistant's selection) to the input, finds the matching devices of the caller's view (at most
    /// <see cref="MaxDevices"/>, by address) and hands the plugin <c>device_ids</c> and <c>devices_total</c> instead of the filter
    /// (read them with <see cref="PluginToolDevices.Of"/>). The input type has no property of these names.
    /// </summary>
    public bool DeviceFilter { get; init; }

    /// <summary>
    /// The id of a task plugin of the same package whose dialog reads what this tool reads: the host lists the tool only for a
    /// caller with at least one permission of that task, like the reads of its dialog (<c>TaskService.Query</c>). A page call
    /// reaches the method without this check, so the plugin checks it again with <see cref="ICallContext.Has"/>. Null: none.
    /// </summary>
    public string? TaskPlugin { get; init; }

    /// <summary>
    /// The answer may hand the caller a file (SDK 0.13.0): a property <c>file</c> of type <see cref="PluginToolFile"/> (null when
    /// there is none) names one of the plugin's downloads (<see cref="ICorePluginDownloads"/>) and its payload. The host never
    /// passes the bytes through the answer: it replaces <c>file</c> with a file reference the caller turns into a one-time
    /// download link (an agent) or a file card with Save (the assistant). The bytes are read only then, through
    /// <c>PluginService.Download</c> as the caller (the download's permission, device group and audit). Never a backup, a
    /// private key or a PKCS#12 file.
    /// </summary>
    public bool ReturnsFile { get; init; }

    /// <summary>The largest file a tool with <see cref="TakesUpload"/> gets.</summary>
    public const int MaxUploadBytes = 1024 * 1024;

    /// <summary>
    /// The tool takes one file the caller uploaded (SDK 0.13.0, docs/spec/mcp.md "Files"): its input has a text property
    /// <c>file_id</c> (an upload of the caller, e.g. through an upload link, so the file never passes a model). The host reads
    /// the upload (at most <see cref="MaxUploadBytes"/>) and hands the plugin <c>file_base64</c> and <c>file_name</c> as well,
    /// in the preview, a check of an entered secret and the run. The input type has no property of these two names.
    /// </summary>
    public bool TakesUpload { get; init; }

    /// <summary>
    /// New serializer options in the style of the host's own tools: names in snake_case ("next_cursor"), enums by their snake_case
    /// name, nulls written, names read in any case. Keep them in a static field of the plugin (<see cref="PluginJson.CreateOptions"/>).
    /// </summary>
    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.SnakeCaseLower));
        return options;
    }

    /// <summary>The method of <see cref="ICorePlugin.InvokeAsync(ICallContext, string, string?, CancellationToken)"/> a read calls, and the run of a change: <c>tool.&lt;name&gt;</c>.</summary>
    public string Method => "tool." + Name;

    /// <summary>The method of the preview of a change: <c>tool.&lt;name&gt;.preview</c>. It never writes.</summary>
    public string PreviewMethod => Method + ".preview";

    /// <summary>The method of the run of a change (the same as <see cref="Method"/>).</summary>
    public string RunMethod => Method;

    /// <summary>True when <paramref name="method"/> is one of this tool's methods.</summary>
    public bool Calls(string method) => string.Equals(method, Method, StringComparison.Ordinal) || string.Equals(method, PreviewMethod, StringComparison.Ordinal);

    /// <summary>True when <paramref name="method"/> belongs to a tool of the host (a plugin names its tool methods with it).</summary>
    public static bool IsToolMethod(string? method) => method is not null && method.StartsWith("tool.", StringComparison.Ordinal);

    /// <summary>True when <paramref name="method"/> only reads or previews (never writes): every tool method but the run of a change.</summary>
    public static bool IsPreviewMethod(string? method) => method is not null && method.EndsWith(".preview", StringComparison.Ordinal) && IsToolMethod(method);
}

/// <summary>
/// The file a tool's answer hands the caller (<see cref="PluginTool.ReturnsFile"/>): which of the plugin's downloads makes it,
/// with which payload, and what the caller is told before it fetches it. Put it into the answer's <c>file</c> property; the
/// host turns it into a file reference. The names of the JSON fields are fixed, whatever the plugin's serializer options.
/// </summary>
/// <param name="Method">The download method (<see cref="ICorePluginDownloads.OpenDownloadAsync(ICallContext, string, string?, CancellationToken)"/>), e.g. "export".</param>
/// <param name="PayloadJson">Its payload as JSON; the download checks it again with the caller's rights.</param>
/// <param name="FileName">The file name the caller is told (no folder); the download's own name wins when the file is fetched.</param>
/// <param name="ContentType">The media type, e.g. "application/pdf", "text/csv", "application/x-pem-file".</param>
/// <param name="Size">The size in bytes when known before the download (an estimate is not a size: leave it null).</param>
public sealed record PluginToolFile(
    [property: System.Text.Json.Serialization.JsonPropertyName("method")] string Method,
    [property: System.Text.Json.Serialization.JsonPropertyName("payload_json")] string? PayloadJson,
    [property: System.Text.Json.Serialization.JsonPropertyName("file_name")] string FileName,
    [property: System.Text.Json.Serialization.JsonPropertyName("content_type")] string ContentType,
    [property: System.Text.Json.Serialization.JsonPropertyName("size")] long? Size = null)
{
    /// <summary>The property of the answer that holds the file.</summary>
    public const string Property = "file";
}

/// <summary>
/// The page of a list tool (<see cref="PluginTool.Paged"/>) as the host hands it to the plugin: how many rows and where to start.
/// </summary>
/// <param name="Limit">Rows of the page, 1 to <see cref="PluginTool.MaxRows"/>.</param>
/// <param name="After">The sort key of the last row of the page before; null for the first page. Rows come in the order of that key.</param>
public sealed record PluginToolPage(int Limit, string? After)
{
    /// <summary>The page of a payload; the first page of the default size for a payload without one.</summary>
    public static PluginToolPage Of(string? payloadJson)
    {
        var limit = PluginTool.DefaultRows;
        string? after = null;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                if (JsonNode.Parse(payloadJson) is JsonObject payload)
                {
                    if (payload["limit"] is JsonValue value && value.TryGetValue<int>(out var given))
                    {
                        limit = Math.Clamp(given, 1, PluginTool.MaxRows);
                    }

                    if (payload["after"] is JsonValue text && text.TryGetValue<string>(out var key) && key.Length > 0)
                    {
                        after = key;
                    }
                }
            }
            catch (JsonException)
            {
                // a payload that is no JSON is the plugin's own problem; the first page
            }
        }

        return new PluginToolPage(limit, after);
    }

    /// <summary>The most bytes of rows <see cref="Take{T}"/> puts into one page, so the whole answer stays below the host's 32 KB.</summary>
    public const int RowBudgetBytes = 24 * 1024;

    /// <summary>
    /// The rows of the page from <paramref name="rows"/> (already after <see cref="After"/>, in the order of the sort key): at most
    /// <see cref="Limit"/> rows and at most <see cref="RowBudgetBytes"/> of JSON (at least one row), and whether more rows follow.
    /// </summary>
    /// <param name="rows">The rows after <see cref="After"/> in order; read only as far as the page needs.</param>
    /// <param name="options">The options the rows are written with.</param>
    public (IReadOnlyList<T> Rows, bool More) Take<T>(IEnumerable<T> rows, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(options);
        var taken = new List<T>();
        var size = 0;
        foreach (var row in rows)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(row, options).Length + 1;
            if (taken.Count >= Limit || (taken.Count > 0 && size + bytes > RowBudgetBytes))
            {
                return (taken, true);
            }

            size += bytes;
            taken.Add(row);
        }

        return (taken, false);
    }

    /// <summary>The sort key of a row compared with <see cref="After"/> in the ordinal order: true when the row comes after it.</summary>
    public bool Includes(string sortKey) => After is null || string.CompareOrdinal(sortKey, After) > 0;

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Limit} after {After ?? "start"}");
}

/// <summary>
/// The devices of a tool with <see cref="PluginTool.DeviceFilter"/> as the host hands them to the plugin: the ids of the caller's
/// devices that match the filter (at most <see cref="PluginTool.MaxDevices"/>) and how many matched.
/// </summary>
/// <param name="Ids">The devices, in the order of their address. Look each one up with the call's <c>Devices</c>: never trust an id.</param>
/// <param name="Total">How many devices matched; more than <see cref="Ids"/> when the filter matched more than the host hands over.</param>
public sealed record PluginToolDevices(IReadOnlyList<Guid> Ids, int Total)
{
    /// <summary>Devices that matched but are not in <see cref="Ids"/>.</summary>
    public int Left => Math.Max(0, Total - Ids.Count);

    /// <summary>The devices of a payload; none for a payload without them.</summary>
    public static PluginToolDevices Of(string? payloadJson)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        var total = 0;
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                if (JsonNode.Parse(payloadJson) is JsonObject payload)
                {
                    foreach (var item in payload["device_ids"] as JsonArray ?? [])
                    {
                        if (item is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) && seen.Add(id))
                        {
                            ids.Add(id);
                        }
                    }

                    total = payload["devices_total"] is JsonValue count && count.TryGetValue<int>(out var n) ? n : ids.Count;
                }
            }
            catch (JsonException)
            {
                // no devices
            }
        }

        return new PluginToolDevices(ids, Math.Max(total, ids.Count));
    }
}

/// <summary>
/// What a tool method of a plugin answers when the arguments are not valid: the host turns it into the refusal of the call, one
/// entry per field (the texts of the plugin's page: "Enter a name."). A method that fails for another reason throws
/// <see cref="InvalidOperationException"/> with the text to show.
/// </summary>
public static class PluginToolErrors
{
    /// <summary>The answer of a tool method for invalid arguments: <c>{"errors": [{"field": "...", "error": "..."}]}</c>.</summary>
    /// <param name="errors">The fields and what to enter instead; the key is the JSON name of the argument, nested with dots.</param>
    public static string Answer(IEnumerable<KeyValuePair<string, string>> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return new JsonObject
        {
            ["errors"] = new JsonArray([.. errors.Select(e => (JsonNode)new JsonObject { ["field"] = e.Key, ["error"] = e.Value })]),
        }.ToJsonString();
    }
}

/// <summary>
/// A secret the run of a plugin tool's change needs (SDK 0.13.0, docs/spec/mcp.md "Secrets"): a password, a passphrase, a key.
/// The caller never sends it as an argument: the preview names it in its answer's <see cref="PluginToolSecrets.Property"/>
/// (a <see cref="PluginToolPlan"/> sets <see cref="PluginToolPlan.SecretsNeeded"/>), the person enters the value in their own
/// shell or the assistant's card, and the host hands it to the run (and to a check of the value) in the payload's
/// <see cref="PluginToolSecrets.ValuesProperty"/>. Never echo a value in an answer, an audit detail or a log line.
/// </summary>
/// <param name="Name">Lower case words with underscores ("password", "pkcs12_password").</param>
/// <param name="Label">What the person is asked for, in lower case ("password", "PKCS#12 password", "license key").</param>
public sealed record PluginToolSecret(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("label")] string Label);

/// <summary>The secrets of a plugin tool's change (<see cref="PluginToolSecret"/>) as the host passes them.</summary>
public static class PluginToolSecrets
{
    /// <summary>The property of a preview's answer that names the secrets the run needs (a list of <see cref="PluginToolSecret"/>); the host takes it out.</summary>
    public const string Property = "secrets_needed";

    /// <summary>The property of the payload that holds the values by name: all of them for the run, one when the host checks a value the person entered.</summary>
    public const string ValuesProperty = "secrets";

    /// <summary>
    /// The secret values of a payload by name; empty for a preview, all of the preview's for a run, the one value being checked
    /// when the host asks the preview method whether an entered value fits (answer a field error at <c>secrets.&lt;name&gt;</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(string? payloadJson)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return values;
        }

        try
        {
            if (JsonNode.Parse(payloadJson) is JsonObject payload && payload[ValuesProperty] is JsonObject secrets)
            {
                foreach (var (name, value) in secrets)
                {
                    if (value is JsonValue text && text.TryGetValue<string>(out var secret))
                    {
                        values[name] = secret;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // no secrets
        }

        return values;
    }
}
