using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Grpc.Core;

using Microsoft.Extensions.Logging;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Auth;
using Oadm.Server.Plugins;
using Oadm.Server.Tasks;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>What the preview of a plugin's change fixed for its run: the plugin, its run method and the arguments (in memory only).</summary>
internal sealed record PlannedPluginRun(string PluginId, string Method, string PayloadJson);

/// <summary>
/// The tools of the core plugins (docs/spec/mcp.md "Plugins"): every running plugin's <see cref="ICorePlugin.Tools"/> in the one
/// registry, named <c>oadm_&lt;plugin area&gt;_&lt;name&gt;</c>, listed only while the plugin is on and the caller has the tool's
/// permission (the plugin's own <c>RequiredPermission</c> and device group rules, then the tool's). A tool calls the plugin through
/// <c>PluginService.Invoke</c> in process with the caller, exactly as the client does (rule 1), so the plugin's role, permission,
/// device group and audit rules apply. Arguments are checked against the strict schema first, answers are at most
/// <see cref="ToolAnswers.MaxBytes"/>, a change is a preview and a run with a handle. A tool without a usable schema is not
/// offered (fail closed).
/// </summary>
internal sealed partial class PluginTools(PluginRegistry registry, CorePluginHost host, Oadm.Core.Auth.RoleStore roles, ToolHandles handles, ToolCursors cursors, ILogger<PluginTools> logger) : IToolSource
{
    public static readonly string InvokePath = InProcessCalls.PathOf(Proto.PluginService.Descriptor, "Invoke");

    public const string Off = "This plugin is turned off.";
    public const string PreviewEnded = "This preview has ended or already ran. Call the preview tool again.";
    public const string TooLarge = "The answer is too large. Narrow the request.";
    public const string PluginFailed = "The plugin could not answer. Try again.";

    private static readonly Regex NamePattern = NameRegex();
    private static readonly Regex AreaPattern = AreaRegex();

    /// <summary>A plugin's tools, built once for as long as the plugin object lives (a plugin's list of tools does not change while it runs).</summary>
    private readonly ConditionalWeakTable<ICorePlugin, List<OadmTool>> _offers = [];

    /// <inheritdoc />
    public IReadOnlyList<OadmTool> Current()
    {
        var tools = new List<OadmTool>();
        foreach (var registered in registry.CorePlugins)
        {
            if (host.GetStatus(registered.Id).State != CorePluginState.Running)
            {
                continue;
            }

            tools.AddRange(_offers.GetValue(registered.Plugin, _ => Build(registered)));
        }

        return tools;
    }

    /// <summary>The area of a plugin's tool names: its own word, else its id without "oadm.".</summary>
    internal static string? AreaOf(ICorePlugin plugin)
    {
        var area = plugin.ToolArea;
        if (string.IsNullOrWhiteSpace(area))
        {
            var id = plugin.Id;
            area = (id.StartsWith("oadm.", StringComparison.OrdinalIgnoreCase) ? id["oadm.".Length..] : id).Replace('-', '_').Replace('.', '_').ToLowerInvariant();
        }

        return AreaPattern.IsMatch(area) ? area : null;
    }

    private List<OadmTool> Build(RegisteredCorePlugin registered)
    {
        var plugin = registered.Plugin;
        var tools = new List<OadmTool>();
        try
        {
            var area = AreaOf(plugin);
            if (area is null)
            {
                LogRefused(plugin.Id, "-", "the tool area is not lower case words with underscores");
                return tools;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tool in plugin.Tools ?? [])
            {
                if (!NamePattern.IsMatch(tool.Name) || tool.Name.Length > PluginTool.MaxNameLength || !seen.Add(tool.Name))
                {
                    LogRefused(plugin.Id, tool.Name, "its name is not unique lower case words with underscores");
                    continue;
                }

                try
                {
                    tools.AddRange(Make(registered, area, tool));
                }
#pragma warning disable CA1031 // One broken tool is left out; the others stay.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    LogRefused(plugin.Id, tool.Name, ex.Message);
                }
            }
        }
#pragma warning disable CA1031 // A plugin that throws on Tools offers none.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogRefused(plugin.Id, "-", ex.Message);
        }

        return tools;
    }

    private IEnumerable<OadmTool> Make(RegisteredCorePlugin registered, string area, PluginTool tool)
    {
        var plugin = registered.Plugin;
        if (tool.Area is { } own && !AreaPattern.IsMatch(own))
        {
            throw new InvalidOperationException("the tool's area is not lower case words with underscores");
        }

        var name = $"oadm_{tool.Area ?? area}_{tool.Name}";
        var input = SchemaOf(tool.InputType, tool.InputSchema, tool.JsonOptions, input: true)
            ?? throw new InvalidOperationException("the input has no usable schema");
        var output = SchemaOf(tool.OutputType, tool.OutputSchema, tool.JsonOptions, input: false)
            ?? throw new InvalidOperationException("the output has no usable schema");
        if (tool.Paged)
        {
            input = Paging(input);
            RenameNext(output);
        }

        if (tool.ReturnsFile)
        {
            WithFile(output);
        }

        string[] methods = [InvokePath];
        if (tool.DeviceFilter)
        {
            input = WithFilter(input);
            methods = [InvokePath, DeviceTools.SearchPath];
        }

        var idempotent = tool.Idempotent ?? !tool.Changes;
        if (!tool.Changes)
        {
            yield return new OadmTool(name, tool.Title, tool.Description, input, output, methods, call => ReadAsync(call, plugin, tool, name))
            {
                Gate = caller => GateOf(registered, tool, tool.Method, caller),
            };
            yield break;
        }

        var runOutput = SchemaOf(tool.RunOutputType, tool.RunOutputSchema, tool.JsonOptions, input: false)
            ?? throw new InvalidOperationException("the run's output has no usable schema");
        if (tool.ReturnsFile)
        {
            WithFile(runOutput);
        }

        var preview = (JsonObject)output.DeepClone();
        var properties = preview["properties"] as JsonObject ?? throw new InvalidOperationException("the output is no object with properties");
        if (properties.ContainsKey("handle") || properties.ContainsKey("expires"))
        {
            throw new InvalidOperationException("the preview's output names handle or expires");
        }

        properties["handle"] = Out("string", "Pass it to " + name + "_run (once, 10 minutes).");
        properties["expires"] = Out("string", "When the handle ends (UTC).");
        if (preview["required"] is JsonArray required)
        {
            required.Add("handle");
            required.Add("expires");
        }

        var runName = name + "_run";
        yield return new OadmTool(
            name + "_preview",
            tool.Title + ": preview",
            tool.Description + " Nothing is changed. Call " + runName + " with the handle to apply it.",
            input,
            preview,
            methods,
            call => PreviewAsync(call, plugin, tool, runName))
        {
            Gate = caller => GateOf(registered, tool, tool.PreviewMethod, caller),
        };

        yield return new OadmTool(
            runName,
            tool.Title + ": run",
            "Applies the plan of " + name + "_preview, which checks everything again.",
            Object(null, [P("handle", Text("The handle of " + name + "_preview (once, 10 minutes).", 64, minLength: 1))], "handle"),
            runOutput,
            [InvokePath],
            call => RunAsync(call, plugin, tool, runName))
        {
            ReadsOnly = false,
            Destructive = tool.Destructive,
            Idempotent = idempotent,
            Gate = caller => GateOf(registered, tool, tool.RunMethod, caller),
        };
    }

    /// <summary>
    /// The schema of a type or of JSON Schema text; null when it cannot be offered. An input takes no property it does not name
    /// (<c>additionalProperties: false</c>); an output is an object with properties. No type and no text: no argument (input only).
    /// </summary>
    private static JsonObject? SchemaOf(Type? type, string? text, JsonSerializerOptions? options, bool input)
    {
        JsonObject? schema;
        if (type is not null)
        {
            schema = TaskPayloadSchemas.Build(type, options);
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            schema = JsonNode.Parse(text) as JsonObject;
            if (schema is not null && !TaskPayloadSchemas.TypesOf(schema).Contains("object"))
            {
                return null;
            }

        }
        else
        {
            return input ? Object(null, []) : null;
        }

        if (schema is null || (input && !(schema["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var allowsOthers) && !allowsOthers)))
        {
            return null;
        }

        schema["properties"] ??= new JsonObject();
        return schema;
    }

    /// <summary>The input of a list: <c>limit</c> and <c>cursor</c> for the caller (the plugin sees <c>limit</c> and <c>after</c>).</summary>
    private static JsonObject Paging(JsonObject input)
    {
        var properties = input["properties"] as JsonObject ?? [];
        properties.Remove("after");
        properties["limit"] = Number("Rows of a page.", 1, ToolAnswers.MaxRows, ToolAnswers.DefaultRows);
        properties["cursor"] = Text("The next_cursor of the page before; leave out for the first page.", ToolAnswers.MaxCursorLength);
        input["properties"] = properties;
        return input;
    }

    /// <summary>The input of a tool on chosen devices: the host's device filter as <c>filter</c> (the plugin sees <c>device_ids</c> and <c>devices_total</c>).</summary>
    private static JsonObject WithFilter(JsonObject input)
    {
        var properties = input["properties"] as JsonObject ?? [];
        if (properties.ContainsKey("filter") || properties.ContainsKey("device_ids") || properties.ContainsKey("devices_total"))
        {
            throw new InvalidOperationException("the input of a tool with a device filter names filter, device_ids or devices_total");
        }

        properties["filter"] = DeviceTools.Filter();
        input["properties"] = properties;
        return input;
    }

    /// <summary>
    /// The payload of a tool on chosen devices: <c>filter</c> found in the caller's view by <c>DeviceService.Search</c> (sorted by
    /// address, at most <see cref="PluginTool.MaxDevices"/>) becomes <c>device_ids</c> and <c>devices_total</c>. An id outside the view
    /// refuses the call there (fail closed); the plugin looks every id up again with the caller.
    /// </summary>
    private static async Task<string> WithDevicesAsync(ToolCall call, string payload)
    {
        var reply = await DeviceTools.SearchAsync(call, new Proto.SearchDevicesRequest
        {
            Filter = DeviceTools.FilterOf(call.Element("filter")),
            Sort = "address",
            Limit = PluginTool.MaxDevices,
        }).ConfigureAwait(false);
        var answer = JsonNode.Parse(payload) as JsonObject ?? [];
        answer.Remove("filter");
        answer["device_ids"] = new JsonArray([.. reply.Devices.Select(d => (JsonNode)JsonValue.Create(d.Id)!)]);
        answer["devices_total"] = reply.Total;
        return answer.ToJsonString();
    }

    /// <summary>The answer of a tool that hands out a file: its <c>file</c> is the host's file reference (or null).</summary>
    private static void WithFile(JsonObject output)
    {
        var properties = output["properties"] as JsonObject ?? throw new InvalidOperationException("the output is no object with properties");
        properties[PluginToolFile.Property] = ToolFiles.OfferSchema("The file this answer hands out; null when there is none. Fetch it with oadm_files_download_link.");
    }

    /// <summary>
    /// The plugin's <see cref="PluginToolFile"/> in <c>file</c> becomes a file reference of the caller: the bytes come later through
    /// <c>PluginService.Download</c> as the caller. Anything else in <c>file</c> is dropped (fail closed).
    /// </summary>
    private JsonObject WithOffer(ToolCall call, string pluginId, JsonObject answer)
    {
        if (!answer.ContainsKey(PluginToolFile.Property))
        {
            return answer;
        }

        var given = answer[PluginToolFile.Property] as JsonObject;
        answer[PluginToolFile.Property] = null;
        if (given?["method"] is JsonValue method && method.TryGetValue<string>(out var name) && name.Length > 0 && !PluginTool.IsToolMethod(name)
            && given["file_name"] is JsonValue file && file.TryGetValue<string>(out var fileName) && fileName.Length > 0)
        {
            var payload = given["payload_json"] is JsonValue p && p.TryGetValue<string>(out var text) ? text : null;
            var type = given["content_type"] is JsonValue t && t.TryGetValue<string>(out var media) && media.Length is > 0 and <= 100 ? media : "application/octet-stream";
            long? size = given["size"] is JsonValue z && z.TryGetValue<long>(out var bytes) && bytes >= 0 ? bytes : null;
            answer[PluginToolFile.Property] = ToolFiles.Offer(call, handles, ToolFiles.PluginFile(call, pluginId, name, payload, Path.GetFileName(fileName), type, size));
        }

        return answer;
    }

    /// <summary>The answer of a list names <c>next</c>; the caller reads <c>next_cursor</c> (the host signs the key into a cursor).</summary>
    private static void RenameNext(JsonObject output)
    {
        var properties = (JsonObject)output["properties"]!;
        var next = properties["next"] as JsonObject ?? throw new InvalidOperationException("the list's output has no next");
        properties.Remove("next");
        properties["next_cursor"] = OrNull(Out("string", "Pass it as cursor for the next page; null at the end."));
        _ = next;
        if (output["required"] is JsonArray required && required.Any(r => r?.GetValue<string>() == "next"))
        {
            required.Remove(required.First(r => r?.GetValue<string>() == "next"));
            required.Add("next_cursor");
        }
    }

    private string? GateOf(RegisteredCorePlugin registered, PluginTool tool, string method, ToolCaller caller)
    {
        if (!registry.TryGetCorePlugin(registered.Id, out var current) || !ReferenceEquals(current.Plugin, registered.Plugin)
            || host.GetStatus(registered.Id).State != CorePluginState.Running)
        {
            return Off;
        }

        if (host.AccessOf(registered.Id, method) is not { } access)
        {
            return Off;
        }

        var user = caller.User;
        if (!user.HasAllDevices && (!current.Origin.SupportsCallContext || RequiresAllDevices(current.Plugin, method)))
        {
            return DeviceGroupTexts.AllDevicesOnly;
        }

        if (access.AdminOnly)
        {
            return user.IsAdmin ? null : AuthInterceptor.AdminOnlyMessage;
        }

        foreach (var permission in new[] { access.Permission, tool.Permission is { } own ? PermissionKeys.Of(registered.Id, own) : null })
        {
            if (permission is not null && !user.Has(permission))
            {
                return roles.Catalog.DeniedMessage(permission);
            }
        }

        if (tool.TaskPlugin is { } taskId)
        {
            // The reads of the task's dialog: any permission of the task, of a task of the same package only (fail closed).
            if (!registry.TryGetTaskPlugin(taskId, out var task)
                || !string.Equals(task.Origin.PackageId, current.Origin.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return Off;
            }

            var keys = PermissionCatalog.TaskKeys(task.Plugin);
            if (!keys.Any(user.Has))
            {
                return roles.Catalog.DeniedMessage(keys[0]);
            }
        }

        return null;
    }

    private static bool RequiresAllDevices(ICorePlugin plugin, string method)
    {
        try
        {
            return plugin.RequiresAllDevices(method);
        }
#pragma warning disable CA1031 // A plugin that cannot answer is for users with all devices (fail closed, like the host).
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    private async Task<JsonObject> ReadAsync(ToolCall call, ICorePlugin plugin, PluginTool tool, string toolName)
    {
        var query = ToolCursors.QueryOf(call.Arguments);
        var payload = PayloadOf(call, tool, toolName, query);
        if (tool.DeviceFilter)
        {
            payload = await WithDevicesAsync(call, payload).ConfigureAwait(false);
        }

        var answer = await InvokeAsync(call, plugin.Id, tool.Method, payload).ConfigureAwait(false);
        if (tool.ReturnsFile)
        {
            answer = WithOffer(call, plugin.Id, answer);
        }

        if (tool.Paged && answer["next"] is JsonValue next && next.TryGetValue<string>(out var key) && key.Length > 0)
        {
            answer.Remove("next");
            answer["next_cursor"] = cursors.Make(call.Caller, toolName + "|" + query, key, string.Empty);
        }
        else if (tool.Paged)
        {
            answer.Remove("next");
            answer["next_cursor"] = null;
        }

        return Sized(answer);
    }

    private async Task<JsonObject> PreviewAsync(ToolCall call, ICorePlugin plugin, PluginTool tool, string runTool)
    {
        var payload = call.Arguments.GetRawText();
        if (tool.DeviceFilter)
        {
            payload = await WithDevicesAsync(call, payload).ConfigureAwait(false); // the run gets the devices the preview planned
        }

        var answer = await InvokeAsync(call, plugin.Id, tool.PreviewMethod, payload).ConfigureAwait(false);
        if (tool.ReturnsFile)
        {
            answer = WithOffer(call, plugin.Id, answer);
        }

        answer["handle"] = handles.AddChange(call.Caller.BindingKey, new PlannedChange(runTool, new PlannedPluginRun(plugin.Id, tool.RunMethod, payload)));
        answer["expires"] = handles.EndOfNew().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return Sized(answer);
    }

    private async Task<JsonObject> RunAsync(ToolCall call, ICorePlugin plugin, PluginTool tool, string runTool)
    {
        var plan = handles.TakeChange<PlannedPluginRun>(call.Caller.BindingKey, call.Text("handle"), runTool)
            ?? throw new ToolException(PreviewEnded, [new ToolFieldError("handle", PreviewEnded)]);
        if (!string.Equals(plan.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(PreviewEnded, [new ToolFieldError("handle", PreviewEnded)]);
        }

        // The plugin checks everything again, as for its page: the role, the permission, the device group, the input.
        var answer = await InvokeAsync(call, plan.PluginId, plan.Method, plan.PayloadJson).ConfigureAwait(false);
        return Sized(tool.ReturnsFile ? WithOffer(call, plan.PluginId, answer) : answer);
    }

    /// <summary>The arguments the plugin gets: as given; a list's cursor is read here into <c>after</c>, and <c>limit</c> is always there.</summary>
    private string PayloadOf(ToolCall call, PluginTool tool, string toolName, string query)
    {
        if (!tool.Paged)
        {
            return call.Arguments.GetRawText();
        }

        var payload = (JsonObject)JsonNode.Parse(call.Arguments.GetRawText())!;
        var place = cursors.Read(call.Caller, call.Text("cursor"), toolName + "|" + query);
        payload.Remove("cursor");
        payload["limit"] = call.Number("limit") ?? ToolAnswers.DefaultRows;
        if (place is { } at)
        {
            payload["after"] = at.Key;
        }

        return payload.ToJsonString();
    }

    private static async Task<JsonObject> InvokeAsync(ToolCall call, string pluginId, string method, string payload)
    {
        // Through the plugin service in process, as the client calls a plugin: it checks the permission of the method, the
        // device view, runs the plugin's call overload and audits an audited method.
        Proto.InvokeReply reply;
        try
        {
            reply = await call.CallAsync<PluginGrpcService, Proto.InvokeRequest, Proto.InvokeReply>(InvokePath,
                new Proto.InvokeRequest { PluginId = pluginId, Method = method, PayloadJson = payload }, (s, r, c) => s.Invoke(r, c)).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Internal)
        {
            throw new ToolException(PluginFailed); // never the plugin's own exception text
        }

        JsonNode? parsed;
        try
        {
            parsed = string.IsNullOrWhiteSpace(reply.PayloadJson) ? null : JsonNode.Parse(reply.PayloadJson);
        }
        catch (JsonException)
        {
            throw new ToolException(PluginFailed);
        }

        if (parsed is not JsonObject answer)
        {
            throw new ToolException(PluginFailed);
        }

        if (answer.Count == 1 && answer["errors"] is JsonArray errors)
        {
            var fields = new List<ToolFieldError>();
            foreach (var item in errors.OfType<JsonObject>().Take(20))
            {
                fields.Add(new ToolFieldError(item["field"]?.GetValue<string>() ?? string.Empty, item["error"]?.GetValue<string>() ?? string.Empty));
            }

            throw new ToolException(ToolAnswers.FieldsMessage, fields);
        }

        return answer;
    }

    private static JsonObject Sized(JsonObject answer) =>
        ToolAnswers.SizeOf(answer) > ToolAnswers.MaxBytes ? throw new ToolException(TooLarge) : answer;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tool {Tool} of the plugin {Plugin} is not offered: {Reason}")]
    private partial void LogRefused(string plugin, string tool, string reason);

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[a-z][a-z0-9]*(_[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex AreaRegex();
}
