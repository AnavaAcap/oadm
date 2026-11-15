using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ActionRules.Agent;

/// <summary>What an agent's run of the Action Rule Editor's task does.</summary>
public enum RuleAgentAction
{
    /// <summary>Changes the rules of a name: turn on or off, rename, wait time, action settings, the recipient.</summary>
    Change = 0,

    /// <summary>Deletes the rules of a name.</summary>
    Delete = 1,

    /// <summary>Copies one rule of a device to the devices, changed by <see cref="RuleAgentPayload.Change"/>.</summary>
    Copy = 2,

    /// <summary>Creates a new rule on the devices.</summary>
    Create = 3,

    /// <summary>Puts back what an earlier change of the editor did.</summary>
    Undo = 4,
}

/// <summary>
/// The input of the Action Rule Editor's task for AI agents (docs/spec/mcp.md "Tasks"): the editor's operations on rules
/// (change, turn on or off, delete, copy, create, undo). The task turns it into the editor's own run (<see cref="RuleTaskPayload"/>)
/// with the page's code: rules by name at the version the editor read last, a copy read from its device with its passwords
/// on the server, the run saved for undo. Passwords are never accepted here (<see cref="RuleAgentCheck.SecretText"/>).
/// </summary>
[Description("Change, turn on or off, delete, copy or create action rules on the devices, or undo a change of the Action Rule Editor. Rules are matched as the editor read them last (oadm_device_rules_list).")]
public sealed record RuleAgentPayload(
    [property: Description("What to do. Turning rules on or off is a change of enabled only.")] RuleAgentAction Action,
    [property: Description("Change and delete: the exact name of the rules (oadm_device_rules_list). Every rule of this name on each device."), MaxLength(RulePatchApply.MaxNameLength)] string? RuleName = null,
    [property: Description("Change: what to set. Copy: what to change on the copies (optional).")] RuleAgentChange? Change = null,
    [property: Description("Copy: the device the rule is copied from.")] Guid? SourceDeviceId = null,
    [property: Description("Copy: the rule id on that device (rule_id of oadm_device_rules_list)."), MaxLength(64)] string? SourceRuleId = null,
    [property: Description("Create: the new rule.")] RuleAgentNewRule? Rule = null,
    [property: Description("Undo: the id of the change to put back (changes of oadm_device_rules_list).")] Guid? UndoOf = null)
{
    /// <summary>The names and enum texts the task reads (camelCase, enums as camelCase names).</summary>
    public static readonly JsonSerializerOptions JsonOptions = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);

    /// <summary>
    /// Whether <paramref name="json"/> is an agent's payload: every run the page starts carries its run id, which this input
    /// has no field for. Null and broken text count as an agent's (its check refuses them).
    /// </summary>
    public static bool IsAgent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            return JsonNode.Parse(json) is not JsonObject root || !root.ContainsKey("runId");
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Reads the payload; an empty or broken one is an <see cref="ArgumentException"/>.</summary>
    public static RuleAgentPayload Parse(string? json) => PluginJson.Parse<RuleAgentPayload>(json, JsonOptions);
}

/// <summary>The parts of a rule a change sets; what is left out stays as each device has it.</summary>
public sealed record RuleAgentChange(
    [property: Description("A new name."), MaxLength(RulePatchApply.MaxNameLength)] string? Name = null,
    [property: Description("True turns the rules on, false off.")] bool? Enabled = null,
    [property: Description("Seconds the rule waits before it can run again; 0 = no wait."), Range(0, RulePatchApply.MaxTimeoutSeconds)] int? TimeoutSeconds = null,
    [property: Description("Settings of the action by name (as the device names them). No passwords: set those in the Action Rule Editor.")] IReadOnlyDictionary<string, string>? Parameters = null,
    [property: Description("The recipient the action sends to, by name; each device uses its own recipient of this name."), MaxLength(256)] string? RecipientName = null)
{
    /// <summary>The editor's patch of this change.</summary>
    public RulePatch ToPatch() => new()
    {
        Name = Name,
        Enabled = Enabled,
        TimeoutSeconds = TimeoutSeconds,
        Parameters = Parameters is { Count: > 0 } ? Parameters : null,
        RecipientName = string.IsNullOrWhiteSpace(RecipientName) ? null : RecipientName,
    };
}

/// <summary>An event of a new rule: its topic and the values it must have.</summary>
public sealed record RuleAgentEvent(
    [property: Description("The event's topic as the device names it (\"tns1:Device/tnsaxis:IO/VirtualInput\", event_topic of oadm_device_rules_list)."), MaxLength(512)] string Topic,
    [property: Description("Values the event must have by item name, e.g. {\"port\": \"20\"}; at most 8.")] IReadOnlyDictionary<string, string>? Values = null)
{
    public NewFilter ToFilter() => RuleAgentFilters.Of(Topic, Values);
}

/// <summary>A further event of a new rule that must be active (its own type: the schema has no references).</summary>
public sealed record RuleAgentCondition(
    [property: Description("The event's topic as the device names it."), MaxLength(512)] string Topic,
    [property: Description("Values the event must have by item name; at most 8.")] IReadOnlyDictionary<string, string>? Values = null)
{
    public NewFilter ToFilter() => RuleAgentFilters.Of(Topic, Values);
}

internal static class RuleAgentFilters
{
    public static NewFilter Of(string? topic, IReadOnlyDictionary<string, string>? values) =>
        new(topic ?? string.Empty, [.. (values ?? new Dictionary<string, string>()).Select(v => new NameValue(v.Key, v.Value))]);
}

/// <summary>A new rule, as the editor's rule builder makes it.</summary>
public sealed record RuleAgentNewRule(
    [property: Description("The rule's name."), MaxLength(RulePatchApply.MaxNameLength)] string Name,
    [property: Description("The event that starts the rule, or while whileActive the event it runs during.")] RuleAgentEvent Event,
    [property: Description("The action as the device names it (\"com.axis.action.fixed.notification.tcp\")."), MaxLength(256)] string Template,
    [property: Description("Every setting of the action by name. No passwords: set those in the Action Rule Editor.")] IReadOnlyDictionary<string, string>? Parameters = null,
    [property: Description("The rule is on.")] bool Enabled = true,
    [property: Description("The rule is active while the event is (for actions that run while the rule is active).")] bool WhileActive = false,
    [property: Description("Further events that must be active."), MaxLength(ActionRulesPlugin.MaxConditions)] IReadOnlyList<RuleAgentCondition>? Conditions = null,
    [property: Description("Only while this schedule is active: a schedule every device has (\"com.axis.schedules.office_hours\"). Leave it out for always."), MaxLength(256)] string? ScheduleId = null,
    [property: Description("Seconds the rule waits before it can run again; 0 = no wait."), Range(0, RulePatchApply.MaxTimeoutSeconds)] int TimeoutSeconds = 0,
    [property: Description("The recipient the action sends to, by name; each device uses its own recipient of this name."), MaxLength(256)] string? RecipientName = null)
{
    /// <summary>The editor's new rule.</summary>
    public NewRule ToNewRule() => new(
        Name ?? string.Empty,
        Enabled,
        Event?.ToFilter()!,
        WhileActive,
        [.. (Conditions ?? []).Select(c => c?.ToFilter()!)],
        string.IsNullOrWhiteSpace(ScheduleId) ? null : ScheduleId,
        TimeoutSeconds,
        Template ?? string.Empty,
        Parameters ?? new Dictionary<string, string>())
    {
        RecipientName = string.IsNullOrWhiteSpace(RecipientName) ? null : RecipientName,
    };
}
