using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Page methods of a core plugin offered as tools (SDK 0.13.0, docs/spec/mcp.md "Plugins"): each entry wraps one method of the
/// plugin's page with a typed, strict input (the record <c>TArgs</c>; its JSON Schema is the tool's input), so a caller does on
/// the page what a user does, with the page's own checks and rights. A change is a preview that runs the page's validation and
/// never writes, and a run that validates again and calls the page's code with the caller. A read, or a start the page does
/// without asking (a check, a scan), is one tool. Never a way to call any page method: only the entries declared here are tools.
/// </summary>
/// <remarks>
/// Keep one instance in a static field of the plugin and hand its <see cref="Tools"/> to <see cref="ICorePlugin.Tools"/>. In
/// the plugin's rules (<see cref="ICorePlugin.RequiredRole"/>, <see cref="ICorePlugin.RequiredPermission"/>,
/// <see cref="ICorePlugin.RequiresAllDevices"/>, <see cref="ICorePlugin.IsAudited"/>) read a tool method as its page method
/// (<see cref="RulesMethodOf"/>), so a tool has exactly the rights of the page; a preview is never audited
/// (<see cref="PluginTool.IsPreviewMethod"/>).
/// </remarks>
/// <typeparam name="TOwner">What the handlers work on (the plugin or its service), handed to <see cref="InvokeAsync"/>.</typeparam>
/// <param name="json">The serializer options of the arguments and answers; null: <see cref="PluginTool.CreateJsonOptions"/>.</param>
public sealed class PluginPageTools<TOwner>(JsonSerializerOptions? json = null)
{
    private readonly Dictionary<string, Entry> _byMethod = new(StringComparer.Ordinal);
    private readonly List<PluginTool> _tools = [];

    /// <summary>The serializer options of every tool here.</summary>
    public JsonSerializerOptions Json { get; } = json ?? PluginTool.CreateJsonOptions();

    /// <summary>The tools, in the order they were added.</summary>
    public IReadOnlyList<PluginTool> Tools => _tools;

    /// <summary>
    /// A read, or a start the page does without asking (a check, a read of devices): one tool <c>tool.&lt;name&gt;</c> that
    /// calls <paramref name="read"/> with the caller. <paramref name="tool"/> gives name, title and description (and
    /// <see cref="PluginTool.Permission"/>, <see cref="PluginTool.DeviceFilter"/>, <see cref="PluginTool.Paged"/>); input and
    /// output come from the type arguments.
    /// </summary>
    /// <param name="tool">The tool's name, title and description.</param>
    /// <param name="pageMethod">The page method whose rules the tool has.</param>
    /// <param name="read">Answers the call; throws <see cref="PluginToolInputException"/> for an invalid argument, <see cref="InvalidOperationException"/> with the page's text for any other refusal.</param>
    public PluginPageTools<TOwner> Read<TArgs, TAnswer>(PluginTool tool, string pageMethod, Func<TOwner, ICallContext, PluginToolInput<TArgs>, CancellationToken, Task<TAnswer>> read)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(read);
        var declared = tool with { InputType = typeof(TArgs), InputSchema = null, OutputType = typeof(TAnswer), OutputSchema = null, JsonOptions = Json, Changes = false };
        return Add(declared, pageMethod, new Entry(declared, pageMethod, typeof(TArgs), async (owner, caller, args, payload, _, ct) =>
            Serialize(await read(owner, caller, Input<TArgs>(args, payload), ct).ConfigureAwait(false))));
    }

    /// <summary>
    /// A change: the host offers <c>&lt;name&gt;_preview</c> and <c>&lt;name&gt;_run</c>. The preview calls
    /// <paramref name="check"/>, the page's validation, which answers the plan and never writes. The run calls
    /// <paramref name="check"/> again and then <paramref name="run"/>, the page's code, with the caller and the plan. Let the
    /// plan derive from <see cref="PluginToolPlan"/> so the confirmation card counts what changes.
    /// </summary>
    /// <param name="tool">The tool's name, title ("Delete reader") and description; <see cref="PluginTool.Destructive"/> for a delete.</param>
    /// <param name="pageMethod">The page method whose rules the tool has (the one <paramref name="run"/> does).</param>
    /// <param name="check">Validates the arguments as the page does and answers what the run will do; never writes. Field errors: <see cref="PluginToolInputException"/>; another refusal: <see cref="InvalidOperationException"/> with the page's text.</param>
    /// <param name="run">Does it, as the page does.</param>
    public PluginPageTools<TOwner> Change<TArgs, TPlan, TResult>(
        PluginTool tool,
        string pageMethod,
        Func<TOwner, ICallContext, PluginToolInput<TArgs>, CancellationToken, Task<TPlan>> check,
        Func<TOwner, ICallContext, PluginToolInput<TArgs>, TPlan, CancellationToken, Task<TResult>> run)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(run);
        var declared = tool with
        {
            InputType = typeof(TArgs),
            InputSchema = null,
            OutputType = typeof(TPlan),
            OutputSchema = null,
            RunOutputType = typeof(TResult),
            RunOutputSchema = null,
            JsonOptions = Json,
            Changes = true,
        };
        return Add(declared, pageMethod, new Entry(declared, pageMethod, typeof(TArgs), async (owner, caller, args, payload, preview, ct) =>
        {
            var input = Input<TArgs>(args, payload);
            var plan = await check(owner, caller, input, ct).ConfigureAwait(false);
            return preview ? Serialize(plan) : Serialize(await run(owner, caller, input, plan, ct).ConfigureAwait(false));
        }));
    }

    /// <summary>True when <paramref name="method"/> is a method of a tool here (a read, a preview or a run).</summary>
    public bool Handles(string? method) => method is not null && _byMethod.ContainsKey(method);

    /// <summary>The page method whose rules a tool method has; null when <paramref name="method"/> is no tool here.</summary>
    public string? PageMethodOf(string? method) => method is not null && _byMethod.TryGetValue(method, out var entry) ? entry.PageMethod : null;

    /// <summary>The method to apply the plugin's rules to: the page method of a tool method here, else <paramref name="method"/> itself.</summary>
    public string RulesMethodOf(string method) => PageMethodOf(method) ?? method;

    /// <summary>The tool of a method here; null when there is none.</summary>
    public PluginTool? ToolOf(string? method) => method is not null && _byMethod.TryGetValue(method, out var entry) ? entry.Tool : null;

    /// <summary>
    /// Answers a tool method: reads the arguments (no text reads like <c>{}</c>, broken JSON is refused), checks their
    /// validation attributes (a field error per property, in the JSON names), and calls the handler with the caller. Field
    /// errors come back as <see cref="PluginToolErrors.Answer"/>.
    /// </summary>
    public async Task<string> InvokeAsync(TOwner owner, ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (method is null || !_byMethod.TryGetValue(method, out var entry))
        {
            throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }

        object args;
        try
        {
            args = JsonSerializer.Deserialize(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson, entry.ArgsType, Json)
                ?? throw new ArgumentException(PluginJson.UnreadableRequest);
        }
        catch (JsonException)
        {
            throw new ArgumentException(PluginJson.UnreadableRequest);
        }

        var errors = AttributeErrors(args);
        if (errors.Count > 0)
        {
            return PluginToolErrors.Answer(errors);
        }

        try
        {
            return await entry.Handle(owner, caller, args, payloadJson, PluginTool.IsPreviewMethod(method), ct).ConfigureAwait(false);
        }
        catch (PluginToolInputException invalid)
        {
            return PluginToolErrors.Answer(invalid.Errors);
        }
    }

    private PluginPageTools<TOwner> Add(PluginTool tool, string pageMethod, Entry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageMethod);
        if (PluginTool.IsToolMethod(pageMethod))
        {
            throw new ArgumentException("A tool wraps a page method, never another tool.", nameof(pageMethod));
        }

        if (_byMethod.ContainsKey(tool.Method) || _byMethod.ContainsKey(tool.PreviewMethod))
        {
            throw new ArgumentException($"The tool '{tool.Name}' is declared twice.", nameof(tool));
        }

        _byMethod[tool.Method] = entry;
        if (tool.Changes)
        {
            _byMethod[tool.PreviewMethod] = entry;
        }

        _tools.Add(tool);
        return this;
    }

    private List<KeyValuePair<string, string>> AttributeErrors(object args)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(args, new ValidationContext(args), results, validateAllProperties: true))
        {
            return [];
        }

        return [.. results.Select(r => new KeyValuePair<string, string>(
            r.MemberNames.FirstOrDefault() is { } member ? Json.PropertyNamingPolicy?.ConvertName(member) ?? member : string.Empty,
            r.ErrorMessage ?? PluginJson.UnreadableRequest))];
    }

    private string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static PluginToolInput<TArgs> Input<TArgs>(object args, string? payloadJson) =>
        new((TArgs)args, PluginToolDevices.Of(payloadJson), PluginToolPage.Of(payloadJson));

    private sealed record Entry(PluginTool Tool, string PageMethod, Type ArgsType, Func<TOwner, ICallContext, object, string?, bool, CancellationToken, Task<string>> Handle);
}

/// <summary>The arguments of a call of a tool of <see cref="PluginPageTools{TOwner}"/>.</summary>
/// <param name="Args">The arguments, read and checked against their validation attributes.</param>
/// <param name="Devices">The devices of a tool with <see cref="PluginTool.DeviceFilter"/> (none for another tool). Look each id up with the caller.</param>
/// <param name="Page">The page of a tool with <see cref="PluginTool.Paged"/> (the first page for another tool).</param>
public sealed record PluginToolInput<TArgs>(TArgs Args, PluginToolDevices Devices, PluginToolPage Page);

/// <summary>
/// Invalid arguments of a tool of <see cref="PluginPageTools{TOwner}"/>: one entry per field (its JSON name, nested with dots) with
/// the page's text ("Enter a name."). The tool answers them as <see cref="PluginToolErrors.Answer"/>.
/// </summary>
public sealed class PluginToolInputException : ArgumentException
{
    /// <summary>One field and what to enter instead.</summary>
    public PluginToolInputException(string field, string error)
        : this([new KeyValuePair<string, string>(field, error)])
    {
    }

    /// <summary>The fields and what to enter instead.</summary>
    public PluginToolInputException(IEnumerable<KeyValuePair<string, string>> errors)
        : base(PluginJson.UnreadableRequest)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = [.. errors];
    }

    /// <summary>A refusal without fields.</summary>
    public PluginToolInputException()
        : this([])
    {
    }

    /// <summary>A refusal with its own text and cause, without fields.</summary>
    public PluginToolInputException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    /// <summary>The fields and their texts.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Errors { get; }
}

/// <summary>
/// The count of a change's plan for the confirmation card (SDK 0.13.0): a plan record of <see cref="PluginPageTools{TOwner}"/>
/// derives from it, so the card says "1 reader" or "3 leases". Its fields are <c>will_change</c> and <c>unit</c>.
/// </summary>
/// <param name="WillChange">How many things the run changes.</param>
/// <param name="Unit">What they are: one word in the singular ("reader", "pipeline", "lease", "device").</param>
public abstract record PluginToolPlan(
    [property: Description("How many things the run changes.")] int WillChange,
    [property: Description("What they are, in the singular.")] string Unit);
