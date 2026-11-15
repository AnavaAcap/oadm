using System.Globalization;
using System.Runtime.CompilerServices;

using Oadm.Plugins.ActionRules.Agent;
using Oadm.Plugins.ActionRules.Rules;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// The editor's operations for AI agents (docs/spec/mcp.md "Tasks"): the host starts the task with a <see cref="RuleAgentPayload"/>
/// on the devices of a preview, and the first task of the run does what the page's method does before its tasks start (the
/// run saved for undo, the rule to copy read from its device, the undo marked), once for the whole run; each task then gets
/// the editor's own payload for its device. The checks of the page's methods that read no device run before any task starts
/// (<see cref="CheckAgent"/>, through <c>ValidatePayload</c>); the plan comes from the editor's last read only.
/// </summary>
public sealed partial class ActionRulesPlugin
{
    // One shared part per run: the host hands every task of a run the same payload string (ConditionalWeakTable: by reference).
    private readonly ConditionalWeakTable<string, AgentRun> _agentRuns = new();

    /// <summary>A device the editor has not read: the agents match rules only as the editor read them.</summary>
    public const string NotReadText = "Not read in the Action Rule Editor. Read the rules there first.";

    /// <summary>A device of the editor's last read without a rule of the asked name.</summary>
    public const string NoSuchRuleText = "No rule with this name.";

    /// <summary>The source device of a copy is skipped (its own rule).</summary>
    public const string SourceText = "The rule is copied from this device.";

    /// <summary>A device an undo of an agent does not touch.</summary>
    public const string NotInChangeText = "Not changed by this change.";

    /// <summary>The newest row of each device the editor read (every table: the device groups' tables too), one pass.</summary>
    internal Dictionary<Guid, DeviceRulesRow> LastReads()
    {
        var rows = new Dictionary<Guid, DeviceRulesRow>();
        foreach (var sweep in _sweeps?.All ?? [])
        {
            foreach (var row in sweep.Snapshot().Rows)
            {
                if (!rows.TryGetValue(row.DeviceId, out var known) || known.ReadUtc < row.ReadUtc)
                {
                    rows[row.DeviceId] = row;
                }
            }
        }

        return rows;
    }

    /// <summary>The rules of <paramref name="name"/> in a row the editor read without problem; null without such a row.</summary>
    private static IReadOnlyList<RuleView>? RulesNamed(DeviceRulesRow? row, string name) =>
        row is null || row.State != RowStates.Ok ? null : [.. row.Rules.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal))];

    /// <summary>
    /// The checks of the page's methods that read no device, at the input's JSON fields with the page's texts. Empty when the
    /// input may run.
    /// </summary>
    internal IReadOnlyList<PayloadError> CheckAgent(RuleAgentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new List<PayloadError>();
        void Unused(string field, bool given)
        {
            if (given)
            {
                errors.Add(new(field, RuleAgentCheck.UnusedText));
            }
        }

        switch (payload.Action)
        {
            case RuleAgentAction.Change or RuleAgentAction.Delete:
                if (string.IsNullOrWhiteSpace(payload.RuleName))
                {
                    errors.Add(new("ruleName", RuleAgentCheck.RuleNameText));
                }

                if (payload.Action == RuleAgentAction.Change)
                {
                    if (payload.Change is null)
                    {
                        errors.Add(new("change", "Choose what to change."));
                    }
                    else
                    {
                        errors.AddRange(CheckChange(payload.Change, required: true));
                    }
                }
                else
                {
                    Unused("change", payload.Change is not null);
                }

                Unused("sourceDeviceId", payload.SourceDeviceId is not null);
                Unused("sourceRuleId", payload.SourceRuleId is not null);
                Unused("rule", payload.Rule is not null);
                Unused("undoOf", payload.UndoOf is not null);
                break;
            case RuleAgentAction.Copy:
                if (payload.SourceDeviceId is null)
                {
                    errors.Add(new("sourceDeviceId", RuleAgentCheck.SourceDeviceText));
                }

                if (string.IsNullOrWhiteSpace(payload.SourceRuleId))
                {
                    errors.Add(new("sourceRuleId", "Choose the rule to copy."));
                }
                else if (payload.SourceDeviceId is { } source)
                {
                    var rule = LastReads().GetValueOrDefault(source) is { State: RowStates.Ok } row ? row.Rules.FirstOrDefault(r => r.Id == payload.SourceRuleId) : null;
                    if (rule is null)
                    {
                        errors.Add(new("sourceRuleId", RuleAgentCheck.SourceNotReadText));
                    }
                    else if (ActionNames.IsRefused(rule.Template))
                    {
                        errors.Add(new("sourceRuleId", ActionNames.RefusedText));
                    }
                }

                if (payload.Change is { } change && !change.ToPatch().IsEmpty)
                {
                    errors.AddRange(CheckChange(change, required: false));
                }

                Unused("ruleName", payload.RuleName is not null);
                Unused("rule", payload.Rule is not null);
                Unused("undoOf", payload.UndoOf is not null);
                break;
            case RuleAgentAction.Create:
                if (payload.Rule is null)
                {
                    errors.Add(new("rule", "Describe the rule."));
                }
                else
                {
                    errors.AddRange(CheckNewRule(payload.Rule));
                }

                Unused("ruleName", payload.RuleName is not null);
                Unused("change", payload.Change is not null);
                Unused("sourceDeviceId", payload.SourceDeviceId is not null);
                Unused("sourceRuleId", payload.SourceRuleId is not null);
                Unused("undoOf", payload.UndoOf is not null);
                break;
            case RuleAgentAction.Undo:
                if (payload.UndoOf is not { } undo)
                {
                    errors.Add(new("undoOf", "Choose the change to undo."));
                }
                else if (Undo.IsAvailable && Undo.DevicesOf(undo).Count == 0)
                {
                    errors.Add(new("undoOf", RuleTexts.NothingToUndo));
                }

                Unused("ruleName", payload.RuleName is not null);
                Unused("change", payload.Change is not null);
                Unused("sourceDeviceId", payload.SourceDeviceId is not null);
                Unused("sourceRuleId", payload.SourceRuleId is not null);
                Unused("rule", payload.Rule is not null);
                break;
            default:
                errors.Add(new("action", "Unknown action."));
                break;
        }

        return errors;
    }

    /// <summary>A change: the patch's own check (<see cref="RulePatch.Problem"/>), the settings the devices know, no password.</summary>
    private List<PayloadError> CheckChange(RuleAgentChange change, bool required)
    {
        var patch = change.ToPatch();
        var errors = new List<PayloadError>();
        if (patch.IsEmpty)
        {
            if (required)
            {
                errors.Add(new("change", "Choose at least one thing to change."));
            }

            return errors;
        }

        if (patch.Problem() is { } problem)
        {
            errors.Add(new(RuleAgentCheck.ChangeFieldOf(problem), problem));
            return errors;
        }

        if (RuleAgentCheck.Secret(patch.Parameters) is { } secret)
        {
            // TODO(secret-handle): take a secret handle of the agents' secret channel (branch feat/agent-transfer) for the
            // password settings of an action instead of refusing them; until then passwords are set in the editor only.
            errors.Add(new("change.parameters." + secret, RuleAgentCheck.SecretText));
        }
        else if (UnknownSetting(patch.Parameters) is { } unknown)
        {
            errors.Add(new("change.parameters", unknown));
        }

        if (change.RecipientName is { } recipient && recipient.Trim().Length is 0 or > 256)
        {
            errors.Add(new("change.recipientName", "Choose the recipient."));
        }

        return errors;
    }

    /// <summary>A new rule: the rule builder's check (<see cref="BuildProblem"/>), no password.</summary>
    private List<PayloadError> CheckNewRule(RuleAgentNewRule rule)
    {
        if (RuleAgentCheck.Secret(rule.Parameters) is { } secret)
        {
            // TODO(secret-handle): as for a change, a secret handle for the action's password settings later.
            return [new("rule.parameters." + secret, RuleAgentCheck.SecretText)];
        }

        var created = Normalize(rule.ToNewRule());
        if (created.Event is null)
        {
            return [new("rule.event", "Choose the event.")];
        }

        return BuildProblem(created) is { } problem ? [new(RuleAgentCheck.RuleFieldOf(problem), problem)] : [];
    }

    /// <summary>
    /// What a run of the input would do, from the editor's last read only: the devices without the rules, the copy's source,
    /// the devices of an undo; deleting says so, a copy says it takes the rule's stored passwords along.
    /// </summary>
    internal TaskPlan PlanAgent(RuleAgentPayload payload, TaskPlan byStatus)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(byStatus);
        var reads = payload.Action is RuleAgentAction.Change or RuleAgentAction.Delete ? LastReads() : [];
        var undoDevices = payload.Action == RuleAgentAction.Undo && payload.UndoOf is { } undo && Undo.IsAvailable ? Undo.DevicesOf(undo).ToHashSet() : [];
        var onlyEnabled = payload.Change?.ToPatch() is { OnlyEnabled: true } p ? p.Enabled : null;
        var rules = 0;
        var several = false;
        var devices = new List<TaskPlanDevice>(byStatus.Devices.Count);
        foreach (var entry in byStatus.Devices)
        {
            if (entry.SkipReason is not null)
            {
                devices.Add(entry);
                continue;
            }

            string? skip = null;
            switch (payload.Action)
            {
                case RuleAgentAction.Change or RuleAgentAction.Delete:
                    var named = RulesNamed(reads.GetValueOrDefault(entry.DeviceId), payload.RuleName!);
                    skip = named is null ? NotReadText
                        : named.Count == 0 ? NoSuchRuleText
                        : onlyEnabled is { } on && named.All(r => r.Enabled == on) ? (on ? "Already on." : "Already off.")
                        : null;
                    if (skip is null)
                    {
                        rules += named!.Count;
                        several |= named.Count > 1;
                    }

                    break;
                case RuleAgentAction.Copy when entry.DeviceId == payload.SourceDeviceId:
                    skip = SourceText;
                    break;
                case RuleAgentAction.Undo when !undoDevices.Contains(entry.DeviceId):
                    skip = NotInChangeText;
                    break;
            }

            devices.Add(skip is null ? entry : entry with { SkipReason = skip });
        }

        var running = devices.Count(d => d.SkipReason is null);
        var warnings = new List<string>();
        switch (payload.Action)
        {
            case RuleAgentAction.Delete when running > 0:
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"Deletes {Count(rules, "action rule")} on {Count(running, "device")}. Undo in the Action Rule Editor puts them back."));
                break;
            case RuleAgentAction.Change when several:
                warnings.Add("Some devices have more than one rule of this name; each of them is changed.");
                break;
            case RuleAgentAction.Copy:
                warnings.Add("The copies get the rule's stored passwords. A device that has the same rule keeps it.");
                break;
            case RuleAgentAction.Undo when undoDevices.Count > devices.Count(d => undoDevices.Contains(d.DeviceId)):
                warnings.Add(string.Create(CultureInfo.InvariantCulture, $"{Count(undoDevices.Count - devices.Count(d => undoDevices.Contains(d.DeviceId)), "device")} of this change are not among the chosen devices and stay as they are."));
                break;
        }

        return byStatus with { Devices = devices, Warnings = warnings };
    }

    private static string Count(int count, string what) =>
        count == 1 ? "1 " + what : string.Create(CultureInfo.InvariantCulture, $"{count:N0} {what}s");

    /// <summary>
    /// The editor's payload of one task of an agent's run. The first task of the run does the run's part once (saved for undo,
    /// the copy read, the undo marked); a failure there fails every task of the run with the page's text ("Nothing was
    /// changed"). Throws <see cref="ArgumentException"/> before any device of the task is read or written.
    /// </summary>
    internal async Task<RuleTaskPayload> AgentPayloadAsync(ICallContext? caller, IDeviceInfo device, string json, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(json);
        if (caller is null)
        {
            throw new ArgumentException(RuleAgentCheck.NoCallerText);
        }

        var input = RuleAgentPayload.Parse(json);
        if (CheckAgent(input) is [var first, ..])
        {
            throw new ArgumentException(first.Message + " Nothing was changed.");
        }

        var holder = _agentRuns.GetValue(json, _ => new AgentRun());
        Task<SharedRun> shared;
        lock (holder)
        {
            // Not cancelled with the first task: the run's part is for every task of the run.
            holder.Shared ??= PrepareRunAsync(caller, input, CancellationToken.None);
            shared = holder.Shared;
        }

        SharedRun run;
        try
        {
            run = await shared.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(Nothing(ex.Message), ex);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw new ArgumentException(Nothing(ex.Message), ex);
        }

        if (input.Action != RuleAgentAction.Undo && await Undo.FindAsync(run.RunId, ct).ConfigureAwait(false) is { Undone: true })
        {
            throw new ArgumentException("This change was undone. Nothing was changed.");
        }

        RuleTaskPayload payload;
        switch (input.Action)
        {
            case RuleAgentAction.Change or RuleAgentAction.Delete:
                var named = RulesNamed(run.Reads.GetValueOrDefault(device.Id), input.RuleName!);
                if (named is not { Count: > 0 })
                {
                    throw new ArgumentException((named is null ? NotReadText : NoSuchRuleText) + " Nothing was changed.");
                }

                var targets = new List<DeviceTargets> { new(device.Id, [.. named.Select(r => new RuleRef(r.Id, r.Version))]) };
                var patch = input.Action == RuleAgentAction.Change ? PreparePatch(input.Change!.ToPatch(), [device.Id]) : null;
                var action = input.Action == RuleAgentAction.Delete ? RuleTaskActions.Delete : RuleTaskActions.Edit;
                payload = new RuleTaskPayload(action, run.RunId, TaskName(action, patch), targets, patch);
                break;
            case RuleAgentAction.Copy:
                if (device.Id == input.SourceDeviceId)
                {
                    throw new ArgumentException(SourceText + " Nothing was changed.");
                }

                var changes = input.Change?.ToPatch() is { IsEmpty: false } copyPatch ? copyPatch : null;
                payload = CopyPayload(run.Rule!, run.Schedules, changes, [device.Id]) with { RunId = run.RunId };
                break;
            case RuleAgentAction.Create:
                payload = BuildPayload(input.Rule!.ToNewRule(), [device.Id]) with { RunId = run.RunId };
                break;
            default:
                if (!run.UndoDevices.Contains(device.Id))
                {
                    throw new ArgumentException(NotInChangeText + " Nothing was changed.");
                }

                payload = run.UndoPayload!;
                break;
        }

        return payload;
    }

    private static string Nothing(string message)
    {
        var cut = message.IndexOf(" (Parameter '", StringComparison.Ordinal); // the page's texts without the argument's name
        message = cut < 0 ? message : message[..cut];
        return message.EndsWith("Nothing was changed.", StringComparison.Ordinal) ? message : message + " Nothing was changed.";
    }

    /// <summary>The run's part of the page's method, once per run.</summary>
    private async Task<SharedRun> PrepareRunAsync(ICallContext caller, RuleAgentPayload input, CancellationToken ct)
    {
        if (input.Action == RuleAgentAction.Undo)
        {
            var (undo, devices) = await MarkUndoAsync(caller, input.UndoOf!.Value, ct).ConfigureAwait(false);
            return new SharedRun(undo.RunId, []) { UndoPayload = undo, UndoDevices = [.. devices] };
        }

        DeviceRule? rule = null;
        IReadOnlyList<DeviceSchedule> schedules = [];
        var reads = input.Action is RuleAgentAction.Change or RuleAgentAction.Delete ? LastReads() : [];
        if (input.Action == RuleAgentAction.Copy)
        {
            var source = input.SourceDeviceId!.Value;
            var row = LastReads().GetValueOrDefault(source);
            var view = row?.Rules.FirstOrDefault(r => r.Id == input.SourceRuleId) ?? throw new ArgumentException(RuleAgentCheck.SourceNotReadText);
            if (await caller.Devices.FindAsync(source, ct).ConfigureAwait(false) is not { } device)
            {
                throw new DevicesNotAvailableException();
            }

            if (DeviceMessages.Refusal(device, refuseUnreachable: true) is { } refusal)
            {
                throw new ArgumentException(refusal);
            }

            var vapix = await caller.Vapix.CreateAsync(source, ct).ConfigureAwait(false);
            var changes = input.Change?.ToPatch() is { IsEmpty: false } p ? p : null;
            (rule, schedules) = await ReadSourceRuleAsync(vapix, view.Id, view.Version, changes, ct).ConfigureAwait(false);
        }

        var runId = Guid.NewGuid();
        var name = _task.GetTaskName(PluginJson.Serialize(input, RuleAgentPayload.JsonOptions));
        await Undo.StartRunAsync(new UndoRun(runId, _options.Time.GetUtcNow(), name, AgentActionOf(input.Action), 0, false) { Scope = caller.DeviceScopeKey }, ct).ConfigureAwait(false);
        return new SharedRun(runId, reads) { Rule = rule, Schedules = schedules };
    }

    private static string AgentActionOf(RuleAgentAction action) => action switch
    {
        RuleAgentAction.Delete => RuleTaskActions.Delete,
        RuleAgentAction.Copy or RuleAgentAction.Create => RuleTaskActions.Deploy,
        RuleAgentAction.Undo => RuleTaskActions.Undo,
        _ => RuleTaskActions.Edit,
    };

    /// <summary>
    /// A task of an agent's run starts writing: the run counts as running until it ends (<see cref="TaskEnded"/>), so Undo waits.
    /// Its queued tasks are not counted; one that starts after an undo of its run refuses (the undone mark).
    /// </summary>
    internal void AgentTaskStarted(Guid runId) =>
        _running.AddOrUpdate(runId, _ => (1, _options.Time.GetUtcNow()), (_, r) => (r.Left + 1, r.Started));

    private sealed class AgentRun
    {
        public Task<SharedRun>? Shared { get; set; }
    }

    /// <summary>What every task of an agent's run shares: its id, the editor's last read, the rule to copy, the undo.</summary>
    private sealed record SharedRun(Guid RunId, Dictionary<Guid, DeviceRulesRow> Reads)
    {
        public DeviceRule? Rule { get; init; }

        public IReadOnlyList<DeviceSchedule> Schedules { get; init; } = [];

        public RuleTaskPayload? UndoPayload { get; init; }

        public HashSet<Guid> UndoDevices { get; init; } = [];
    }
}

/// <summary>Texts and field names of the agents' input check.</summary>
public static class RuleAgentCheck
{
    /// <summary>A password setting in an agent's input. Passwords are set in the editor (a secret handle comes later).</summary>
    public const string SecretText = "Set passwords in the Action Rule Editor.";

    public const string RuleNameText = "Enter the name of the rules.";

    public const string SourceDeviceText = "Choose the device to copy from.";

    public const string SourceNotReadText = "The Action Rule Editor has not read this rule. Read the rules there first.";

    public const string UnusedText = "Leave this out for this action.";

    public const string NoCallerText = "This task needs the user who started it. Nothing was changed.";

    /// <summary>The first setting that holds a password (<see cref="RuleText.IsSecret"/>), or null.</summary>
    public static string? Secret(IReadOnlyDictionary<string, string>? parameters) =>
        parameters?.Keys.FirstOrDefault(RuleText.IsSecret);

    /// <summary>The field of a change's problem (<see cref="RulePatch.Problem"/>).</summary>
    public static string ChangeFieldOf(string problem) =>
        problem.StartsWith("Enter a name", StringComparison.Ordinal) ? "change.name"
        : problem.StartsWith("Enter 0 to", StringComparison.Ordinal) ? "change.timeoutSeconds"
        : problem.StartsWith("A value", StringComparison.Ordinal) ? "change.parameters"
        : "change";

    /// <summary>The field of a new rule's problem (<see cref="ActionRulesPlugin.BuildProblem"/>).</summary>
    public static string RuleFieldOf(string problem) => problem switch
    {
        _ when problem.StartsWith("Enter a name", StringComparison.Ordinal) => "rule.name",
        _ when problem.StartsWith("Enter 0 to", StringComparison.Ordinal) => "rule.timeoutSeconds",
        _ when problem.StartsWith("A rule can have at most", StringComparison.Ordinal) => "rule.conditions",
        "Choose the event." or "A value of the event cannot be used." or "Choose a schedule as the schedule, not as an event." => "rule.event",
        "Choose the action." or "No device offers this action." or ActionNames.RefusedText => "rule.template",
        _ when problem.StartsWith("Actions that run while", StringComparison.Ordinal) => "rule.whileActive",
        "A setting of the action is too long." => "rule.parameters",
        _ when problem.StartsWith("No device has the setting", StringComparison.Ordinal) => "rule.parameters",
        "Choose the recipient." => "rule.recipientName",
        "Choose the schedule." or "Enter the schedule." => "rule.scheduleId",
        _ => "rule",
    };
}
