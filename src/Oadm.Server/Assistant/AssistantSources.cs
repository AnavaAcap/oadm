using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Server.Tools;

namespace Oadm.Server.Assistant;

/// <summary>
/// The sources of an answer (docs/spec/assistant.md "What the panel shows"): one short chip per area of the tools the answer
/// used, in the order of first use ("7 devices", "Compliance", "Docs"). The devices count where a device tool says how many
/// matched; previews are left out (their card shows). Plain labels only, never a tool's input or answer.
/// </summary>
internal sealed class AssistantSources
{
    /// <summary>The most chips under one answer.</summary>
    public const int MaxChips = 6;

    private readonly List<string> _areas = [];
    private int? _devices;

    /// <summary>A tool that answered (refused calls do not count).</summary>
    public void Add(OadmTool tool, JsonObject answer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(answer);
        if (AssistantTools.IsPreview(tool.Name))
        {
            return;
        }

        var area = AreaOf(tool);
        if (area == Devices)
        {
            var count = tool.Name == "oadm_devices_get" ? 1 : answer["total"] is JsonValue v && v.TryGetValue<int>(out var total) ? total : (int?)null;
            if (count is { } n)
            {
                _devices = Math.Max(_devices ?? 0, n);
            }
        }

        if (!_areas.Contains(area, StringComparer.Ordinal))
        {
            _areas.Add(area);
        }
    }

    /// <summary>The chip of the Axis docs online (a lookup the user said yes to read a page).</summary>
    public const string AxisDocs = "Axis docs";

    /// <summary>An area that is no tool of the registry (the provider's web tools of a lookup).</summary>
    public void AddArea(string area)
    {
        if (!_areas.Contains(area, StringComparer.Ordinal))
        {
            _areas.Add(area);
        }
    }

    /// <summary>The chips; empty when no tool answered.</summary>
    public IReadOnlyList<string> Chips() =>
        [.. _areas.Select(a => a == Devices && _devices is { } n ? DevicesText(n) : a).Take(MaxChips)];

    private const string Devices = "Devices";

    internal static string DevicesText(int count) => count == 1 ? "1 device" : string.Create(CultureInfo.InvariantCulture, $"{count:N0} devices");

    /// <summary>The short name of a tool's area; a plugin's tool its title.</summary>
    internal static string AreaOf(OadmTool tool) => tool.Name switch
    {
        "oadm_devices_snapshot" => "Camera picture",
        SelectionTools.Name => Devices,
        DocsTools.Name => "Docs",
        "oadm_server_info" => "Server",
        "oadm_logs_audit" => "Audit log",
        "oadm_events_log" => "Event log",
        _ when tool.Name.StartsWith("oadm_devices_", StringComparison.Ordinal) => Devices,
        _ when tool.Name.StartsWith("oadm_compliance_", StringComparison.Ordinal) => "Compliance",
        _ when tool.Name.StartsWith("oadm_tasks_", StringComparison.Ordinal) || tool.Name.StartsWith("oadm_task_", StringComparison.Ordinal) => "Tasks",
        _ when tool.Name.StartsWith("oadm_logs_", StringComparison.Ordinal) => "Logs",
        _ when tool.Name.StartsWith("oadm_rules_", StringComparison.Ordinal) => "Event rules",
        _ when tool.Name.StartsWith("oadm_sites_", StringComparison.Ordinal) => "Sites",
        _ when tool.Name.StartsWith("oadm_tags_", StringComparison.Ordinal) => "Tags",
        _ when tool.Name.StartsWith("oadm_files_", StringComparison.Ordinal) => "Files",
        _ when tool.Name.StartsWith("oadm_settings_", StringComparison.Ordinal) => "Settings",
        _ => tool.Title,
    };
}
