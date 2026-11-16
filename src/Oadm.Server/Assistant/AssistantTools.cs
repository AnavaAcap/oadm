using System.Text.Json;

using Microsoft.Extensions.AI;

using Oadm.Server.Tools;

namespace Oadm.Server.Assistant;

/// <summary>
/// The tools the model gets (docs/spec/assistant.md rule 2): the registry's tools of the caller that read or preview. Never a
/// run tool, never a tool that changes, starts tasks or hands out a link (links need the MCP server's file address): a change
/// is always a preview whose Run is the user's click.
/// </summary>
internal static class AssistantTools
{
    public const string PreviewSuffix = "_preview";
    public const string RunSuffix = "_run";

    /// <summary>The tools of <paramref name="caller"/> the model may call, in the registry's order.</summary>
    public static IReadOnlyList<OadmTool> Offered(ToolRegistry registry, ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return [.. registry.For(caller).Where(IsOffered)];
    }

    /// <summary>A read or a preview: never a run, a change, a task start or a link.</summary>
    public static bool IsOffered(OadmTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.ReadsOnly && !tool.StartsTasks && !tool.Destructive
            && !tool.Name.EndsWith(RunSuffix, StringComparison.Ordinal)
            && !tool.Name.EndsWith("_link", StringComparison.Ordinal);
    }

    public static bool IsPreview(string name) => name.EndsWith(PreviewSuffix, StringComparison.Ordinal);

    /// <summary>The run tool of a preview: <c>oadm_tags_assign_preview</c> to <c>oadm_tags_assign_run</c>.</summary>
    public static string RunOf(string preview) => preview[..^PreviewSuffix.Length] + RunSuffix;

    /// <summary>The short line the panel shows while a tool runs.</summary>
    public static string LineOf(OadmTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name switch
        {
            "oadm_devices_summary" or "oadm_devices_search" or "oadm_devices_get" or SelectionTools.Name => "Looking at devices",
            "oadm_devices_snapshot" => "Taking a camera picture",
            "oadm_vulnerabilities_summary" or "oadm_vulnerabilities" or "oadm_vulnerability" => "Checking vulnerabilities",
            DocsTools.Name => "Searching the docs",
            "oadm_tasks_list" or "oadm_tasks_get" => "Looking at tasks",
            "oadm_tasks_catalog" => "Looking at the tasks the devices can run",
            "oadm_logs_search" or "oadm_logs_audit" or "oadm_events_log" => "Reading the logs",
            "oadm_waiting_list" => "Looking at the waiting list",
            "oadm_compliance_devices" => "Looking at compliance results",
            "oadm_compliance_kinds" => "Looking at the kinds of check",
            "oadm_recipients_types" or "oadm_recipients_certificates" => "Looking at recipients",
            "oadm_task_query" => "Reading the device",
            "oadm_devices_optics" or "oadm_devices_ptz" => "Reading the camera",
            "oadm_update_check" => "Checking for a new version",
            "oadm_backups" => "Looking at backups",
            "oadm_backups_download" => "Preparing the backup file",
            "oadm_support_report" => "Preparing the support report",
            "oadm_plugins_optional" => "Looking at optional plugins",
            "oadm_users_list" => "Looking at users",
            "oadm_roles_list" or "oadm_roles_permissions" => "Looking at roles",
            "oadm_device_groups_list" => "Looking at device groups",
            "oadm_access_keys_list" => "Looking at access keys",
            "oadm_settings_get" => "Reading the server settings",
            "oadm_server_status" => "Looking at the server",
            "oadm_sites_list" or "oadm_sites_waiting" => "Looking at sites",
            "oadm_assistant_test" => "Testing the provider",
            "oadm_devices_certificate_trust_preview" => "Reading the device certificates",
            _ when IsPreview(tool.Name) => "Preparing the plan",
            _ => tool.Title,
        };
    }

    /// <summary>The declaration of a tool for the model: its name, description and input schema (OADM runs it, never the library).</summary>
    public static AITool Declare(OadmTool tool) => new Declaration(tool);

    private sealed class Declaration(OadmTool tool) : AIFunctionDeclaration
    {
        private readonly JsonElement _schema = JsonSerializer.SerializeToElement(tool.Input);

        public override string Name => tool.Name;

        public override string Description => tool.Description;

        public override JsonElement JsonSchema => _schema;
    }
}
