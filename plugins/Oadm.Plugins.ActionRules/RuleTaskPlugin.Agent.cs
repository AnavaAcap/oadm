using Oadm.Plugins.ActionRules.Agent;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.ActionRules;

/// <summary>
/// The task's input for AI agents (SDK 0.13.0, docs/spec/mcp.md "Tasks"): <see cref="RuleAgentPayload"/>. The page's own runs
/// carry their run id and start only from the page (<see cref="PagePermissions"/> null for them); an agent's input names the
/// rights of the page's method it stands for.
/// </summary>
public sealed partial class RuleTaskPlugin
{
    /// <summary>The agents' input; the page's payload stays its own (it holds the rule to copy with its passwords).</summary>
    public Type? PayloadType => typeof(RuleAgentPayload);

    /// <summary>The names and enum texts of <see cref="RuleAgentPayload"/>.</summary>
    public System.Text.Json.JsonSerializerOptions? PayloadJsonOptions => RuleAgentPayload.JsonOptions;

    /// <summary>
    /// The page's rights: delete needs "delete", every other operation (change, on and off, copy, create, undo) "manage", as
    /// the page's methods. A run the page started (its run id) answers null: it starts only from the page. An input that
    /// cannot be read needs both.
    /// </summary>
    public IReadOnlyList<string>? PagePermissions(string? payloadJson)
    {
        if (!RuleAgentPayload.IsAgent(payloadJson))
        {
            return null;
        }

        try
        {
            return RuleAgentPayload.Parse(payloadJson).Action switch
            {
                RuleAgentAction.Delete => [ActionRulesMethods.DeletePermission],
                RuleAgentAction.Change or RuleAgentAction.Copy or RuleAgentAction.Create or RuleAgentAction.Undo => [ActionRulesMethods.ManagePermission],
                _ => [ActionRulesMethods.ManagePermission, ActionRulesMethods.DeletePermission],
            };
        }
        catch (ArgumentException)
        {
            return [ActionRulesMethods.ManagePermission, ActionRulesMethods.DeletePermission];
        }
    }

    /// <summary>
    /// The checks of the page's methods that read no device, at the input's fields with the page's texts. A payload of the page
    /// (its run id) gets the task's own check (<see cref="Validate"/>).
    /// </summary>
    public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        if (!RuleAgentPayload.IsAgent(payloadJson))
        {
            try
            {
                Validate(ActionRulesJson.Deserialize<RuleTaskPayload>(payloadJson));
                return [];
            }
            catch (ArgumentException ex)
            {
                return [new(string.Empty, ex.Message)];
            }
        }

        RuleAgentPayload payload;
        try
        {
            payload = RuleAgentPayload.Parse(payloadJson);
        }
        catch (ArgumentException ex)
        {
            return [new(string.Empty, ex.Message)];
        }

        return _owner is null ? [new(string.Empty, "The Action Rule Editor is not running.")] : _owner.CheckAgent(payload);
    }

    /// <summary>From the editor's last read only (<see cref="ActionRulesPlugin.PlanAgent"/>); a page payload plans by status.</summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct)
    {
        var byStatus = TaskPlan.ByCanRun(this, devices);
        if (_owner is null || !RuleAgentPayload.IsAgent(payloadJson))
        {
            return Task.FromResult(byStatus);
        }

        return Task.FromResult(_owner.PlanAgent(RuleAgentPayload.Parse(payloadJson), byStatus));
    }

    /// <summary>The task name of an agent's input, as the page names its runs.</summary>
    private string AgentTaskName(RuleAgentPayload payload) => payload.Action switch
    {
        RuleAgentAction.Delete => ActionRulesPlugin.TaskName(RuleTaskActions.Delete, null),
        RuleAgentAction.Undo => ActionRulesPlugin.TaskName(RuleTaskActions.Undo, null),
        RuleAgentAction.Copy => Short48("Copy action rule " + (payload.Change?.Name?.Trim() ?? SourceName(payload) ?? string.Empty)).TrimEnd(),
        RuleAgentAction.Create => Short48("Create action rule " + (payload.Rule?.Name ?? string.Empty).Trim()),
        _ => ActionRulesPlugin.TaskName(RuleTaskActions.Edit, payload.Change?.ToPatch()),
    };

    /// <summary>The name of the rule to copy as the editor read it; null when it was not read.</summary>
    private string? SourceName(RuleAgentPayload payload) =>
        payload.SourceDeviceId is { } source && _owner?.LastReads().GetValueOrDefault(source) is { } row
            ? row.Rules.FirstOrDefault(r => r.Id == payload.SourceRuleId)?.Name
            : null;

    private static string Short48(string name) => name.Length > 48 ? name[..47] + "…" : name;
}
