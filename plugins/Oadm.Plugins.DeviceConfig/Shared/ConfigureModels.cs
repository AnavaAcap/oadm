using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Ids, names and limits of Configure devices and Create configuration file.</summary>
public static class ConfigureIds
{
    public const string Configure = "oadm.device-config.configure";

    public const string ConfigureName = "Configure devices";

    public const string Create = "oadm.device-config.create";

    public const string CreateName = "Create configuration file";

    /// <summary>Query: parameters (definitions with the device's values), action rules, schedules and stream profiles.</summary>
    public const string ReadQuery = "read";

    /// <summary>Query: the parameter definitions only (a device of another model or AXIS OS of the selection).</summary>
    public const string DefinitionsQuery = "definitions";

    /// <summary>Devices the dialog reads at the same time (one per model and AXIS OS).</summary>
    public const int ReadParallelism = 4;

    /// <summary>Parameter groups read from a device at the same time.</summary>
    public const int GroupParallelism = 4;

    /// <summary>Most parameters, rules and profiles one run may set (a whole device has about 1,000 writable parameters).</summary>
    public const int MaxParameters = 5000;

    public const int MaxRules = 200;

    public const int MaxStreamProfiles = 100;

    /// <summary>A stream profile name the device accepts (streamprofile.cgi: 1..64 characters).</summary>
    public const int MaxStreamProfileName = 64;

    public static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");
}

/// <summary>The permission keys of Configure devices ("task:oadm.device-config.configure:&lt;key&gt;").</summary>
public static class ConfigurePermissions
{
    public const string Configure = "configure";

    /// <summary>The parameters <see cref="DangerousParameters"/> warns about; without it they are not offered.</summary>
    public const string Dangerous = "dangerous";
}

/// <summary>The type of a device parameter (param.cgi <c>listdefinitions</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParamKind>))]
public enum ParamKind
{
    Text,
    Number,
    Bool,
    Enum,
    Hostname,
    Ip,
    IpList,
    Password,
}

/// <summary>One choice of an enum parameter: the device value and its display text.</summary>
public sealed record ParamChoice(string Value, string? Text = null)
{
    public string Display => string.IsNullOrEmpty(Text) ? Value : Text;
}

/// <summary>
/// A writable parameter of a device: its full name ("Image.I0.Appearance.Compression"), the device's display name
/// ("Compression"), its type with range or choices, and the device's current value (empty for passwords).
/// </summary>
public sealed record ParamDefinition(string Name, ParamKind Kind)
{
    public string? NiceName { get; init; }

    public string? Value { get; init; }

    public long? Min { get; init; }

    public long? Max { get; init; }

    public int? MaxLength { get; init; }

    public IReadOnlyList<ParamChoice> Choices { get; init; } = [];

    /// <summary>The device's text for true and false of a bool ("yes" / "no", "true" / "false", "password" / ...).</summary>
    public string? TrueValue { get; init; }

    public string? FalseValue { get; init; }

    /// <summary>The group part of the name ("Image.I0.Appearance").</summary>
    [JsonIgnore]
    public string Group => Name.LastIndexOf('.') is var dot and > 0 ? Name[..dot] : string.Empty;

    /// <summary>The last part of the name ("Compression").</summary>
    [JsonIgnore]
    public string ShortName => Name.LastIndexOf('.') is var dot and >= 0 ? Name[(dot + 1)..] : Name;

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? ShortName : NiceName!;
}

/// <summary>A name and a value (parameters of a file, of an action, of a run).</summary>
public sealed record NameValue(string Name, string Value);

/// <summary>A schedule (recurrence) of the device's event service: id, name and iCalendar text.</summary>
public sealed record ConfigSchedule(string Id, string Name, string Definition)
{
    /// <summary>The schedules every device has (<c>com.axis.schedules.office_hours</c> ...); never created or removed.</summary>
    [JsonIgnore]
    public bool IsBuiltIn => IsBuiltInId(Id);

    public static bool IsBuiltInId(string id) =>
        id.StartsWith("com.axis.schedules.", StringComparison.Ordinal) && !id.StartsWith("com.axis.schedules.genid.", StringComparison.Ordinal);
}

/// <summary>An event filter of an action rule: the topic and an optional message filter.</summary>
public sealed record ConfigEventFilter(string Topic, string? Message);

/// <summary>The action of a rule, copied in like ADM does: name, template and its parameters.</summary>
public sealed record ConfigAction(string Name, string Template, IReadOnlyList<NameValue> Parameters)
{
    /// <summary>The device's configuration id when read from a device or a file (informational; never written).</summary>
    public string? Id { get; init; }
}

/// <summary>An action rule with its action (ADM's <c>ActionRule</c>).</summary>
public sealed record ConfigActionRule(string Name, bool Enabled, ConfigEventFilter? StartEvent, IReadOnlyList<ConfigEventFilter> Conditions, ConfigAction Action)
{
    /// <summary>How long the rule stays active after its start event, exactly as the device or file says it; null = not set.</summary>
    public string? ActivationTimeout { get; init; }

    /// <summary>The own (not built-in) schedule ids the rule's filters name.</summary>
    [JsonIgnore]
    public IEnumerable<string> ScheduleIds =>
        new[] { StartEvent }.Concat(Conditions).OfType<ConfigEventFilter>()
            .SelectMany(f => ScheduleRefs.Find(f.Message))
            .Where(id => !ConfigSchedule.IsBuiltInId(id))
            .Distinct(StringComparer.Ordinal);
}

/// <summary>A stream profile (streamprofile.cgi): name, description and its stream parameters.</summary>
public sealed record ConfigStreamProfile(string Name, string? Description, string Parameters)
{
    public bool IsDefault { get; init; }
}

/// <summary>The words of an event as the Action rules tab shows them.</summary>
public static class EventNames
{
    /// <summary>"tns1:UserAlarm/tnsaxis:Recurring/Pulse" -> "Recurring pulse".</summary>
    public static string Of(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var parts = topic.Split('/').Select(p => p.Contains(':', StringComparison.Ordinal) ? p[(p.IndexOf(':', StringComparison.Ordinal) + 1)..] : p).ToList();
        var tail = parts.Skip(Math.Max(0, parts.Count - 2)).ToList();
        var text = string.Join(' ', tail.Select(Words));
        return text.Length == 0 ? topic : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }

    private static string Words(string part) =>
        string.Concat(part.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(part[i - 1]) ? " " + c : c.ToString()));
}

/// <summary>Schedule ids inside a rule's message filter (<c>@Value="com.axis.schedules.genid.id-0"</c>).</summary>
public static class ScheduleRefs
{
    private const string Prefix = "com.axis.schedules.";

    public static IEnumerable<string> Find(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
        {
            yield break;
        }

        var at = 0;
        while ((at = filter.IndexOf(Prefix, at, StringComparison.Ordinal)) >= 0)
        {
            var end = at;
            while (end < filter.Length && filter[end] is not ('"' or '\'' or ' ' or ')' or ']'))
            {
                end++;
            }

            yield return filter[at..end];
            at = end;
        }
    }

    /// <summary>
    /// The filter with each schedule id replaced by its new id (ids not in the map stay), in one pass over the filter: a new
    /// id that equals another old id is never replaced a second time (genid ids are numbered on every device).
    /// </summary>
    public static string? Rewrite(string? filter, IReadOnlyDictionary<string, string> newIds)
    {
        ArgumentNullException.ThrowIfNull(newIds);
        if (string.IsNullOrEmpty(filter) || newIds.Count == 0)
        {
            return filter;
        }

        var result = new System.Text.StringBuilder(filter.Length);
        var at = 0;
        while (true)
        {
            var start = filter.IndexOf(Prefix, at, StringComparison.Ordinal);
            if (start < 0)
            {
                result.Append(filter, at, filter.Length - at);
                return result.ToString();
            }

            var end = start;
            while (end < filter.Length && filter[end] is not ('"' or '\'' or ' ' or ')' or ']'))
            {
                end++;
            }

            var id = filter[start..end];
            result.Append(filter, at, start - at).Append(newIds.TryGetValue(id, out var renewed) ? renewed : id);
            at = end;
        }
    }
}

/// <summary>Answer of the read and definitions queries: what one device has, or why it could not be read.</summary>
public sealed record DeviceSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string? Model { get; init; }

    public string? Firmware { get; init; }

    public string? Serial { get; init; }

    /// <summary>The writable parameters with their definitions (and values for the read query).</summary>
    public IReadOnlyList<ParamDefinition> Parameters { get; init; } = [];

    public IReadOnlyList<ConfigActionRule> Rules { get; init; } = [];

    public IReadOnlyList<ConfigSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<ConfigStreamProfile> StreamProfiles { get; init; } = [];

    /// <summary>Why the device could not be read at all; null when it could.</summary>
    public string? Error { get; init; }

    /// <summary>Parts that could not be read ("Action rules: ..."); the rest is usable.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static DeviceSettings Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new DeviceSettings { Error = "The server sent no answer." }
            : JsonSerializer.Deserialize<DeviceSettings>(json, Options) ?? new DeviceSettings { Error = "The server sent no answer." };
}

/// <summary>
/// The payload of Configure devices: what is applied to every selected device. Kept in memory only (it may hold passwords);
/// never logged.
/// </summary>
public sealed record ConfigurePayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The names the payload is read with (the agents' schema of <see cref="ConfigureMcpPayload"/> is exported with them).</summary>
    public static JsonSerializerOptions JsonOptions => Options;

    public IReadOnlyList<NameValue> Parameters { get; init; } = [];

    public IReadOnlyList<ConfigActionRule> Rules { get; init; } = [];

    /// <summary>The own schedules the rules use (built-in ones are not sent).</summary>
    public IReadOnlyList<ConfigSchedule> Schedules { get; init; } = [];

    public IReadOnlyList<ConfigStreamProfile> StreamProfiles { get; init; } = [];

    /// <summary>The parameters in an agent's payload (<see cref="ConfigureMcpPayload.Settings"/>); <see cref="Check"/> moves them to <see cref="Parameters"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<NameValue>? Settings { get; init; }

    /// <summary>The action rules in an agent's payload (<see cref="ConfigureMcpPayload.ActionRules"/>); <see cref="Check"/> moves them to <see cref="Rules"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ConfigActionRule>? ActionRules { get; init; }

    /// <summary>
    /// The payload came in the agent's form (<see cref="Settings"/>, <see cref="ActionRules"/>): its secret values are the ones
    /// the person entered through the secret channel (<see cref="SecretsOf"/>, docs/spec/mcp.md "Secrets"); the task never
    /// writes a device password parameter of it whose name is no secret name (that value would be the agent's own).
    /// </summary>
    [JsonIgnore]
    public bool FromAgent { get; init; }

    /// <summary>"10.0.0.48 (AXIS P3265-V)" or the file name; for the log.</summary>
    public string? Source { get; init; }

    /// <summary>
    /// The device the settings were taken from, when it is one: it keeps its own action rules, schedules and stream profiles
    /// (writing them back would only remove and recreate them); its parameters are set like on every device.
    /// </summary>
    public Guid? SourceDeviceId { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Parameters.Count == 0 && Rules.Count == 0 && StreamProfiles.Count == 0;

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Longest text OADM sends in a rule, schedule or stream profile field.</summary>
    public const int MaxTextLength = 4096;

    /// <summary>Most parameters of one action.</summary>
    public const int MaxActionParameters = 100;

    public const string NothingToApply = "Nothing to apply.";

    public const string TooMany = "Too many settings in one run.";

    public const string BadParameter = "A parameter has no name or is listed twice.";

    public const string BadRule = "An action rule has no name, no action or an incomplete event.";

    public const string SameRuleName = "Two action rules have the same name.";

    public const string BadSchedule = "A schedule is incomplete.";

    public const string BadProfile = "A stream profile has no name or a name longer than 64 characters.";

    public const string SameProfileName = "Two stream profiles have the same name.";

    public const string BadText = "A value is too long or has control characters.";

    /// <summary>A device password parameter of an agent's payload whose name is no secret name: its value would be the agent's own.</summary>
    public const string SetPasswordInOadm = "Set the password in OADM.";

    /// <summary>A secret value of an agent's payload the person has not entered (at the run: never, the host hands every one).</summary>
    public const string EnterPassword = "Enter the password.";

    /// <summary>Most secret values of one agent's run (the secret channel's slots of one preview).</summary>
    public const int MaxSecretValues = 8;

    public const string TooManySecrets = "Set at most 8 passwords in one run.";

    /// <summary>
    /// The secrets of an agent's payload (<see cref="Oadm.Sdk.Plugins.ITaskPlugin.SecretsOf"/>): every setting and action parameter with a secret
    /// name (pass, secret, community), whose value the person enters: <c>setting_&lt;i&gt;</c> at <c>settings[i].value</c>,
    /// <c>rule_&lt;i&gt;_parameter_&lt;j&gt;</c> at <c>actionRules[i].action.parameters[j].value</c>. The dialog's form has none
    /// (the person typed its values). Empty for a payload that cannot be read.
    /// </summary>
    public static IReadOnlyList<TaskSecret> SecretsOf(string? json)
    {
        ConfigurePayload? read;
        try
        {
            read = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ConfigurePayload>(json, Options);
        }
        catch (JsonException)
        {
            return [];
        }

        if (read is null || (read.Settings is null && read.ActionRules is null))
        {
            return [];
        }

        var secrets = new List<TaskSecret>();
        var settings = read.Settings ?? [];
        for (var i = 0; i < settings.Count; i++)
        {
            if (settings[i] is { Name: { Length: > 0 } name } && SecretNames.IsSecret(name))
            {
                secrets.Add(new TaskSecret(string.Create(CultureInfo.InvariantCulture, $"setting_{i}"), SecretLabel(name), At("settings", i, "value")));
            }
        }

        var rules = read.ActionRules ?? [];
        for (var i = 0; i < rules.Count; i++)
        {
            var parameters = rules[i]?.Action?.Parameters ?? [];
            for (var j = 0; j < parameters.Count; j++)
            {
                if (parameters[j] is { Name: { Length: > 0 } name } && SecretNames.IsSecret(name))
                {
                    secrets.Add(new TaskSecret(string.Create(CultureInfo.InvariantCulture, $"rule_{i}_parameter_{j}"), SecretLabel(name),
                        string.Create(CultureInfo.InvariantCulture, $"actionRules[{i}].action.parameters[{j}].value")));
                }
            }
        }

        return secrets;
    }

    /// <summary>"password for root.Mqtt.Password" (the last part of the name when that is too long; at most 64 characters).</summary>
    public static string SecretLabel(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string Prefix = "password for ";
        var label = Prefix + name;
        if (label.Length > 64)
        {
            label = Prefix + name[(name.LastIndexOf('.') + 1)..];
        }

        return label.Length > 64 ? label[..64] : label;
    }

    /// <summary>A payload with both forms.</summary>
    public const string BothForms = "Send settings and actionRules, or parameters and rules, not both.";

    /// <summary>"An action rule uses a schedule that is not included (com.axis.schedules.genid.id-0)."</summary>
    public static string MissingSchedule(string id) => $"An action rule uses a schedule that is not included ({id}).";

    /// <summary>Reads and checks the payload; throws <see cref="ArgumentException"/> with the first problem's text.</summary>
    public static ConfigurePayload Parse(string? json)
    {
        var (payload, errors) = Check(json);
        return errors.Count > 0 ? throw new ArgumentException(errors[0].Message) : payload!;
    }

    /// <summary>
    /// Reads and checks the payload of the dialog or of an agent (<see cref="ConfigureMcpPayload"/>): the problems of the first
    /// check that fails, each at its JSON field (<c>settings[2].name</c>), with the texts <see cref="Parse"/> throws. The agent's
    /// form also refuses device addresses (<see cref="DangerousParameters.AddressNote"/>) and values no parameter takes; a
    /// secret's value is the person's (<see cref="SecretsOf"/>), an empty one is refused ("Enter the password."). Reads only
    /// the payload, never a device.
    /// </summary>
    public static (ConfigurePayload? Payload, IReadOnlyList<PayloadError> Errors) Check(string? json) => Check(json, requireSecrets: true);

    /// <summary>
    /// <see cref="Check(string?)"/>; with <paramref name="requireSecrets"/> false an agent's empty secret value is no problem (the
    /// plan of a preview, before the person entered it). With it, an empty one is refused at its field ("Enter the password."),
    /// after every other check passed, so a preview that ignores those fields still sees every other problem.
    /// </summary>
    public static (ConfigurePayload? Payload, IReadOnlyList<PayloadError> Errors) Check(string? json, bool requireSecrets)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Fail(string.Empty, NothingToApply);
        }

        ConfigurePayload? read;
        try
        {
            read = JsonSerializer.Deserialize<ConfigurePayload>(json, Options);
        }
        catch (JsonException)
        {
            return Fail(string.Empty, "The settings could not be read.");
        }

        if (read is null)
        {
            return Fail(string.Empty, NothingToApply);
        }

        var agent = read.Settings is not null || read.ActionRules is not null;
        if (agent && (read.Parameters is { Count: > 0 } || read.Rules is { Count: > 0 } || read.Source is not null || read.SourceDeviceId is not null))
        {
            return Fail(string.Empty, BothForms);
        }

        var payload = read with
        {
            Parameters = (agent ? read.Settings : read.Parameters) ?? [],
            Rules = (agent ? read.ActionRules : read.Rules) ?? [],
            Schedules = read.Schedules ?? [],
            StreamProfiles = read.StreamProfiles ?? [],
            Settings = null,
            ActionRules = null,
            FromAgent = agent,
        };
        if (payload.IsEmpty)
        {
            return Fail(string.Empty, NothingToApply);
        }

        var parameters = agent ? "settings" : "parameters";
        var rules = agent ? "actionRules" : "rules";
        var errors = new List<PayloadError>();
        if (payload.Parameters.Count > ConfigureIds.MaxParameters)
        {
            errors.Add(new(parameters, TooMany));
        }

        if (payload.Rules.Count > ConfigureIds.MaxRules)
        {
            errors.Add(new(rules, TooMany));
        }

        if (payload.StreamProfiles.Count > ConfigureIds.MaxStreamProfiles)
        {
            errors.Add(new("streamProfiles", TooMany));
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < payload.Parameters.Count; i++)
        {
            var p = payload.Parameters[i];
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Value is null || !names.Add(p.Name))
            {
                errors.Add(new(At(parameters, i, "name"), BadParameter));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var missing = new List<PayloadError>();
        if (agent)
        {
            if (payload.Parameters.Count(p => SecretNames.IsSecret(p.Name)) + payload.Rules.Sum(r => r?.Action?.Parameters?.Count(p => p is not null && SecretNames.IsSecret(p.Name ?? string.Empty)) ?? 0) > MaxSecretValues)
            {
                return Fail(parameters, TooManySecrets);
            }

            for (var i = 0; i < payload.Parameters.Count; i++)
            {
                var p = payload.Parameters[i];
                if (DangerousParameters.IsAddress(p.Name))
                {
                    errors.Add(new(At(parameters, i, "name"), DangerousParameters.AddressNote));
                }
                else if (SecretNames.IsSecret(p.Name) && p.Value.Length == 0)
                {
                    missing.Add(new(At(parameters, i, "value"), EnterPassword)); // the person enters it (SecretsOf)
                }
                else if (ValueProblem(p.Value) is { } problem)
                {
                    errors.Add(new(At(parameters, i, "value"), problem));
                }
            }

            if (errors.Count > 0)
            {
                return (null, errors);
            }
        }

        for (var i = 0; i < payload.Rules.Count; i++)
        {
            var r = payload.Rules[i];
            if (r is null || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Action?.Template)
                || r.Conditions is null || r.Action.Parameters is null || r.Action.Parameters.Count > MaxActionParameters
                || r.Action.Parameters.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Value is null)
                || new[] { r.StartEvent }.Concat(r.Conditions).Any(f => f is not null && string.IsNullOrWhiteSpace(f.Topic)))
            {
                errors.Add(new(At(rules, i), BadRule));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var ruleNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < payload.Rules.Count; i++)
        {
            if (!ruleNames.Add(payload.Rules[i].Name))
            {
                errors.Add(new(At(rules, i, "name"), SameRuleName));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        for (var i = 0; agent && i < payload.Rules.Count; i++)
        {
            var actionParameters = payload.Rules[i].Action.Parameters;
            for (var j = 0; j < actionParameters.Count; j++)
            {
                if (SecretNames.IsSecret(actionParameters[j].Name) && actionParameters[j].Value.Length == 0)
                {
                    // A password of an action (a recipient's): the person enters it (SecretsOf).
                    missing.Add(new(string.Create(CultureInfo.InvariantCulture, $"{rules}[{i}].action.parameters[{j}].value"), EnterPassword));
                }
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        if (payload.Schedules.Count > ConfigureIds.MaxRules)
        {
            return Fail("schedules", BadSchedule);
        }

        for (var i = 0; i < payload.Schedules.Count; i++)
        {
            var s = payload.Schedules[i];
            if (s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Definition) || s.Name is null)
            {
                errors.Add(new(At("schedules", i), BadSchedule));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        // Every own schedule a rule uses must come along: a rule never points to an id the device may not have.
        var known = payload.Schedules.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < payload.Rules.Count; i++)
        {
            if (payload.Rules[i].ScheduleIds.FirstOrDefault(id => !known.Contains(id)) is { } unknownSchedule)
            {
                errors.Add(new(At(rules, i), MissingSchedule(unknownSchedule)));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        for (var i = 0; i < payload.StreamProfiles.Count; i++)
        {
            var p = payload.StreamProfiles[i];
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > ConfigureIds.MaxStreamProfileName || p.Parameters is null)
            {
                errors.Add(new(At("streamProfiles", i, "name"), BadProfile));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        var profileNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < payload.StreamProfiles.Count; i++)
        {
            if (!profileNames.Add(payload.StreamProfiles[i].Name))
            {
                errors.Add(new(At("streamProfiles", i, "name"), SameProfileName));
            }
        }

        if (errors.Count > 0)
        {
            return (null, errors);
        }

        if (Texts(payload).Any(t => t.Length > MaxTextLength || t.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
        {
            return Fail(string.Empty, BadText);
        }

        return requireSecrets && missing.Count > 0 ? (null, missing) : (payload, []);
    }

    private static (ConfigurePayload?, IReadOnlyList<PayloadError>) Fail(string field, string message) => (null, [new(field, message)]);

    private static string At(string list, int index, string? field = null) =>
        string.Create(CultureInfo.InvariantCulture, $"{list}[{index}]") + (field is null ? string.Empty : "." + field);

    /// <summary>
    /// What no parameter of a device takes, checked without its definition (<see cref="ParamRules.Problem"/> checks the rest on
    /// each device when the task runs): too long, line breaks and control characters. The dialog's texts.
    /// </summary>
    internal static string? ValueProblem(string value) =>
        value.Length > ParamRules.MaxTextLength
            ? string.Create(CultureInfo.InvariantCulture, $"At most {ParamRules.MaxTextLength} characters.")
            : value.Any(c => char.IsControl(c) && c != '\t') ? "Line breaks and control characters are not allowed." : null;

    /// <summary>Every text of the rules, schedules and profiles (for the length and character check).</summary>
    private static IEnumerable<string> Texts(ConfigurePayload payload) =>
        payload.Rules.SelectMany(r => new[] { r.Name, r.ActivationTimeout ?? string.Empty, r.Action.Name, r.Action.Template }
                .Concat(r.Action.Parameters.SelectMany(p => new[] { p.Name, p.Value }))
                .Concat(new[] { r.StartEvent }.Concat(r.Conditions).OfType<ConfigEventFilter>().SelectMany(f => new[] { f.Topic, f.Message ?? string.Empty })))
            .Concat(payload.Schedules.SelectMany(s => new[] { s.Id, s.Name, s.Definition }))
            .Concat(payload.StreamProfiles.SelectMany(p => new[] { p.Name, p.Description ?? string.Empty, p.Parameters }));

    /// <summary>
    /// "Configure 12 parameters", "Configure 1 action rule"; with several kinds the short nouns keep it within the 48
    /// characters of a task name: "Configure 12 parameters, 1 rule, 3 profiles".
    /// </summary>
    public string TaskName()
    {
        var kinds = (Parameters.Count > 0 ? 1 : 0) + (Rules.Count > 0 ? 1 : 0) + (StreamProfiles.Count > 0 ? 1 : 0);
        var parts = new List<string>();
        if (Parameters.Count > 0)
        {
            parts.Add(ConfigureIds.Count(Parameters.Count, "parameter"));
        }

        if (Rules.Count > 0)
        {
            parts.Add(ConfigureIds.Count(Rules.Count, kinds > 1 ? "rule" : "action rule"));
        }

        if (StreamProfiles.Count > 0)
        {
            parts.Add(ConfigureIds.Count(StreamProfiles.Count, kinds > 1 ? "profile" : "stream profile"));
        }

        return "Configure " + string.Join(", ", parts);
    }
}
