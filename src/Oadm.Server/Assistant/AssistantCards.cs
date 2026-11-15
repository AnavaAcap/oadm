using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Assistant;

/// <summary>
/// The card of a preview (docs/spec/assistant.md "What the panel shows"): what Run does, taken from the preview's answer and
/// the input its handle fixed, never from the model's text. One builder per preview tool of the host; every other preview (a
/// plugin's) lists the answer's plain fields. Values are data: each line is cut to <see cref="MaxLine"/> characters, at most
/// <see cref="MaxLines"/> lines and then "+N more".
/// </summary>
internal static class AssistantCards
{
    public const int MaxLine = 200;
    public const int MaxLines = 12;
    public const int MaxNames = 20;

    /// <summary>Keys of a payload that never show on a card (a task's input is data; a secret never is shown).</summary>
    private static readonly string[] SecretWords = ["password", "secret", "token", "passphrase", "key", "credential", "psk"];

    /// <summary>Fields of an answer the card shows elsewhere (or never).</summary>
    private static readonly HashSet<string> Shown = new(StringComparer.Ordinal)
    {
        "handle", "expires", "note", "warnings", "skipped", "cannot", "first", "devices", "will_run", "will_change", "will_cancel", "will_try", "unit",
        "needs_secrets", "secret_links",
    };

    public static Proto.AssistantPreviewCard Build(OadmTool preview, OadmTool? run, string handle, JsonObject answer, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(answer);
        var card = new Proto.AssistantPreviewCard
        {
            Handle = handle,
            Title = Text(answer["task_name"]) is { Length: > 0 } taskName ? taskName : run is not null ? TitleOf(run.Title) : preview.Title,
            ExpiresUtc = Text(answer["expires"]) ?? string.Empty,
        };
        var details = new List<string>();
        var (count, unit) = (Number(answer["will_run"]) ?? Number(answer["will_change"]) ?? Number(answer["devices"]) ?? 0, "device");
        switch (preview.Name)
        {
            case "oadm_task_preview":
                Add(details, "Task", Text(answer["task_name"]));
                if (arguments.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in input.EnumerateObject().Where(f => !IsSecret(f.Name)))
                    {
                        Add(details, Label(field.Name), Plain(field.Value));
                    }
                }

                foreach (var file in (answer["files"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    Add(details, "File", Text(file["name"]));
                }

                break;
            case "oadm_rules_save_preview":
                Add(details, "Rule", Text(answer["name"]));
                Add(details, Text(answer["change"]) == "create" ? "Creates the rule" : "Changes the rule", Flag(answer["enabled"]) == true ? "on" : "off");
                Add(details, "Watches", Text(answer["watches"]));
                Add(details, "Does", Text(answer["does"]));
                foreach (var destination in (answer["destinations"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = Text(destination["name"]);
                    Add(details, "Sends to", Text(destination["target"]) + (string.IsNullOrEmpty(name) ? string.Empty : " (" + name + ")"));
                }

                Add(details, "Problem", Text(answer["problem"]));
                (count, unit) = (1, "rule");
                break;
            case "oadm_rules_delete_preview":
                Add(details, "Deletes the rule", Text(answer["name"]));
                Add(details, "Watches", Text(answer["watches"]));
                Add(details, "Does", Text(answer["does"]));
                Add(details, "State", Text(answer["state"]));
                (count, unit) = (1, "rule");
                card.Destructive = true;
                break;
            case "oadm_tags_assign_preview":
                // The tag names are the input the handle fixed; the answer counts what changes.
                Add(details, "Adds tags", Names(arguments, "add"));
                Add(details, "Removes tags", Names(arguments, "remove"));
                Add(details, "New tags", string.Join(", ", (answer["new_tags"] as JsonArray ?? []).Select(Text).OfType<string>()));
                card.Destructive = Flag(answer["destructive"]) == true;
                break;
            case "oadm_files_delete_preview":
                var files = (answer["files"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                foreach (var file in files)
                {
                    Add(details, "Deletes the file", Text(file["name"]));
                }

                (count, unit) = (files.Count, "file");
                card.Destructive = true;
                break;
            case "oadm_devices_remove_preview":
                Add(details, "Removes from OADM", Count(count, "device") + " with their stored logins and tags");
                card.Destructive = true;
                break;
            case "oadm_devices_refresh_preview":
                Add(details, "Reads again", Count(count, "device"));
                break;
            case "oadm_devices_credentials_preview":
                Add(details, "Login", Text(answer["credential"]));
                break;
            case "oadm_devices_add_preview":
                Add(details, "Looked at", Text(answer["source"]));
                Add(details, "Found", Number(answer["found"])?.ToString("N0", CultureInfo.InvariantCulture));
                Add(details, "Logs in with the credential list and adds", Number(answer["will_try"])?.ToString("N0", CultureInfo.InvariantCulture));
                if (Number(answer["keep_waiting"]) is > 0 and var waiting)
                {
                    Add(details, "Keeps on the waiting list", waiting.ToString("N0", CultureInfo.InvariantCulture));
                }

                count = Number(answer["will_try"]) ?? Number(answer["keep_waiting"]) ?? 0;
                break;
            case "oadm_tasks_cancel_preview":
                (count, unit) = (Number(answer["will_cancel"]) ?? 0, "task");
                Add(details, "Cancels", Count(count, "task"));
                card.Destructive = true;
                break;
            case "oadm_tasks_delete_preview":
                (count, unit) = (Number(answer["will_delete"]) ?? 0, "task");
                Add(details, "Deletes from the task history", Count(count, "task"));
                if (Number(answer["running"]) is > 0 and var running)
                {
                    Add(details, "Cancels first", Count(running, "task"));
                }

                card.Destructive = true;
                break;
            case "oadm_tasks_delete_all_preview":
                (count, unit) = (Number(answer["will_delete"]) ?? 0, "task");
                Add(details, "Deletes the whole task history", Count(count, "task"));
                card.Destructive = true;
                break;
            case "oadm_tags_update_preview":
                Add(details, "Tag", Text(answer["tag"]));
                Add(details, "New name", Text(answer["new_name"]) is { } renamed && renamed != Text(answer["tag"]) ? renamed : null);
                Add(details, "Color", Text(answer["color"]));
                Add(details, "Used by device groups", Joined(answer["device_groups"]));
                break;
            case "oadm_tags_delete_preview":
                Add(details, "Deletes the tag", Text(answer["tag"]));
                Add(details, "Used by device groups", Joined(answer["device_groups"]));
                card.Destructive = true;
                break;
            case "oadm_waiting_remove_preview":
                (count, unit) = (Number(answer["will_remove"]) ?? 0, "address");
                Add(details, "Removes from the waiting list", Count(count, unit));
                foreach (var entry in (answer["first"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    Add(details, "Address", Text(entry["address"]));
                }

                card.Destructive = true;
                break;
            case "oadm_rules_enable_preview":
                Add(details, Flag(answer["enabled"]) == true ? "Turns on the rule" : "Turns off the rule", Text(answer["name"]));
                Add(details, "Watches", Text(answer["watches"]));
                Add(details, "Does", Text(answer["does"]));
                (count, unit) = (1, "rule");
                break;
            case "oadm_rules_duplicate_preview":
                Add(details, "Copies the rule", Text(answer["name"]));
                Add(details, "Copy", Text(answer["copy_name"]) + ", off");
                (count, unit) = (1, "rule");
                break;
            case "oadm_rules_test_action_preview":
                Add(details, "Rule", Text(answer["name"]));
                Add(details, "Sends once now", Text(answer["action"]));
                Add(details, "Sends to", Joined(answer["sends_to"]));
                (count, unit) = (1, "action");
                break;
            case "oadm_events_clear_log_preview":
                (count, unit) = (Number(answer["will_delete"]) ?? 0, "entry");
                Add(details, "Deletes from the event log", Count(count, unit));
                card.Destructive = true;
                break;
            case "oadm_recipients_save_preview":
                Add(details, Text(answer["change"]) == "create" ? "Creates the recipient" : "Changes the recipient", Text(answer["name"]));
                Add(details, "Type", Text(answer["type"]));
                Add(details, "Sends to", Text(answer["target"]));
                Add(details, "Used by", Joined(answer["used_by"]));
                (count, unit) = (1, "recipient");
                break;
            case "oadm_recipients_delete_preview":
                Add(details, "Deletes the recipient", Text(answer["name"]));
                Add(details, "Sends to", Text(answer["target"]));
                (count, unit) = (1, "recipient");
                card.Destructive = true;
                break;
            case "oadm_recipients_test_preview":
                Add(details, "Tests the recipient", Text(answer["name"]));
                Add(details, "Sends to", Text(answer["target"]));
                (count, unit) = (1, "recipient");
                break;
            case "oadm_compliance_enable_preview":
                Add(details, Flag(answer["enabled"]) == true ? "Turns on" : "Turns off", "Compliance for the server");
                (count, unit) = (Number(answer["checks_on"]) ?? 0, "check");
                card.Destructive = Flag(answer["enabled"]) != true;
                break;
            case "oadm_compliance_check_save_preview":
                Add(details, Text(answer["change"]) == "create" ? "Creates the check" : "Changes the check", Text(answer["name"]));
                Add(details, "Kind", Text(answer["kind"]));
                Add(details, "Asks for", Text(answer["setting"]));
                Add(details, "Severity", Text(answer["severity"]));
                Add(details, "State", Flag(answer["enabled"]) == true ? "on" : "off");
                (count, unit) = (1, "check");
                break;
            case "oadm_compliance_check_delete_preview":
                Add(details, "Deletes the check", Text(answer["name"]));
                (count, unit) = (1, "check");
                card.Destructive = true;
                break;
            case "oadm_compliance_check_enable_preview":
                Add(details, Flag(answer["enabled"]) == true ? "Turns on the check" : "Turns off the check", Text(answer["name"]));
                (count, unit) = (1, "check");
                break;
            case "oadm_compliance_check_now_preview":
                Add(details, "Checks again now", Joined(answer["checks"]));
                (count, unit) = (Number(answer["devices"]) ?? 0, "device");
                break;
            case "oadm_sites_rename_preview":
                Add(details, "Renames the site", Text(answer["name"]));
                Add(details, "New name", Text(answer["new_name"]));
                (count, unit) = (1, "site");
                break;
            case "oadm_devices_optics_preview":
            case "oadm_devices_ptz_preview":
                Add(details, "Moves", Text(answer["change"]));
                (count, unit) = (1, "device");
                break;
            case "oadm_devices_rtsp_port_preview":
                Add(details, "RTSP port now", Number(answer["port_now"])?.ToString(CultureInfo.InvariantCulture) ?? "different");
                Add(details, "RTSP port after the run", Number(answer["port"])?.ToString(CultureInfo.InvariantCulture));
                break;
            case "oadm_vulnerabilities_update_preview":
                Add(details, "Fetches", "the newest list of Axis vulnerabilities");
                Add(details, "List of", Text(answer["updated"]));
                (count, unit) = (1, "list");
                break;
            case "oadm_license_import_preview":
                Add(details, "Imports", "the OADM PRO license key you enter here");
                Add(details, "License now", Text(answer["text"]));
                (count, unit) = (1, "license");
                break;
            case "oadm_license_remove_preview":
                Add(details, "Removes", "the OADM PRO license key");
                Add(details, "License now", Text(answer["text"]));
                (count, unit) = (1, "license");
                card.Destructive = true;
                break;
            case "oadm_license_trial_preview":
                Add(details, "Starts", "the 30-day trial of OADM PRO");
                (count, unit) = (1, "license");
                break;
            case "oadm_plugins_enable_preview":
                Add(details, Flag(answer["enabled"]) == true ? "Turns on" : "Turns off", Text(answer["name"]));
                (count, unit) = (1, "plugin");
                card.Destructive = Flag(answer["enabled"]) != true;
                break;
            case "oadm_plugins_license_preview":
                Add(details, "Sets the license of", Text(answer["name"]));
                Add(details, "License now", Text(answer["license"]));
                (count, unit) = (1, "plugin");
                break;
            case "oadm_plugins_license_remove_preview":
                Add(details, "Removes the license of", Text(answer["name"]));
                Add(details, "License now", Text(answer["license"]));
                (count, unit) = (1, "plugin");
                card.Destructive = true;
                break;
            case "oadm_plugins_optional_install_preview":
                Add(details, "Installs", Text(answer["name"]));
                Add(details, "File", Text(answer["file"]?["name"]));
                Add(details, "SHA-256", Text(answer["file"]?["sha256"]));
                (count, unit) = (1, "plugin");
                break;
            case "oadm_plugins_optional_remove_preview":
                Add(details, "Removes", Text(answer["name"]));
                (count, unit) = (1, "plugin");
                card.Destructive = true;
                break;
            case "oadm_plugins_optional_restart_preview":
                Add(details, "Restarts the server for", Joined(answer["waiting"]));
                (count, unit) = (1, "server");
                card.Destructive = true;
                break;
            case "oadm_backups_schedule_preview":
                Add(details, "Automatic backups", Flag(answer["enabled"]) == true
                    ? string.Create(CultureInfo.InvariantCulture, $"on, daily at {Text(answer["time_of_day"])}, keep {Number(answer["keep"]) ?? 0:N0}")
                    : "off");
                Add(details, "New password", Flag(answer["new_password"]) == true ? "yes" : null);
                Add(details, "Deletes", Number(answer["deleted"]) is > 0 and var gone ? Count(gone, "automatic backup") : null);
                (count, unit) = (1, "schedule");
                card.Destructive = Number(answer["deleted"]) is > 0;
                break;
            case "oadm_backups_create_preview":
                Add(details, "Makes", "a backup of this server, encrypted with the password you enter here");
                (count, unit) = (1, "backup");
                break;
            case "oadm_backups_delete_preview":
                Add(details, "Deletes the backup", Text(answer["name"]));
                (count, unit) = (1, "backup");
                card.Destructive = true;
                break;
            case "oadm_backups_restore_preview":
                Add(details, "Restores", Text(answer["source"]));
                Add(details, "Replaces", "everything on this server");
                (count, unit) = (1, "backup");
                card.Destructive = true;
                break;
            case "oadm_update_install_preview":
                Add(details, "Installs", Text(answer["version"]));
                Add(details, "Replaces", Text(answer["server_version"]));
                (count, unit) = (1, "server");
                card.Destructive = true;
                break;
            default:
                // A plugin's preview: its plain fields, one level deep (the PKI certificate, a VAPIX Commander command).
                foreach (var (name, value) in answer)
                {
                    if (Shown.Contains(name))
                    {
                        continue;
                    }

                    if (value is JsonObject nested)
                    {
                        foreach (var (inner, innerValue) in nested.Where(p => p.Value is JsonValue && !IsSecret(p.Key)))
                        {
                            Add(details, Label(name) + ", " + Label(inner).ToLowerInvariant(), Plain(innerValue));
                        }
                    }
                    else if (!IsSecret(name))
                    {
                        Add(details, Label(name), Plain(value));
                    }
                }

                card.Destructive = run?.Destructive ?? false;
                if (Text(answer["unit"]) is { Length: > 0 } pluginUnit && Number(answer["will_change"]) is { } pluginCount)
                {
                    (count, unit) = (pluginCount, pluginUnit); // a plan of PluginToolPlan: "1 reader", "3 leases"
                }

                break;
        }

        card.DeviceCount = unit == "device" ? count : 0;
        card.CountText = Count(count, unit);
        if (details.Count > MaxLines)
        {
            var more = details.Count - (MaxLines - 1);
            details = [.. details.Take(MaxLines - 1), string.Create(CultureInfo.InvariantCulture, $"+{more:N0} more")];
        }

        card.Details.AddRange(details);
        var skippedUnit = unit == "task" ? "task" : "device";
        foreach (var reason in (answer["skipped"] as JsonArray ?? answer["cannot"] as JsonArray ?? []).OfType<JsonObject>())
        {
            card.Skipped.Add(Cut(Count(Number(reason["count"]) ?? 0, skippedUnit) + ": " + Text(reason["reason"])));
        }

        card.Warnings.AddRange((answer["warnings"] as JsonArray ?? []).Select(Text).OfType<string>().Take(MaxLines).Select(Cut));
        var firstRows = answer["first"] as JsonArray ?? (answer["devices"] as JsonArray);
        foreach (var row in (firstRows ?? []).OfType<JsonObject>().Take(MaxNames))
        {
            card.FirstDevices.Add(Cut(NameOf(row)));
        }

        return card;
    }

    /// <summary>"3 devices", "1 rule", "2 entries", "4 addresses".</summary>
    public static string Count(int count, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? unit : PluralOf(unit))}");

    private static string PluralOf(string unit) =>
        unit.EndsWith('y') ? unit[..^1] + "ies" : unit.EndsWith('s') ? unit + "es" : unit + "s";

    /// <summary>A list of texts of an answer joined, null when empty.</summary>
    private static string? Joined(JsonNode? list) =>
        list is JsonArray array && array.Count > 0 ? string.Join(", ", array.Select(Text).OfType<string>()) : null;

    /// <summary>"Ping: run" of a plugin's run tool becomes "Ping".</summary>
    public static string TitleOf(string runTitle) => runTitle.EndsWith(": run", StringComparison.Ordinal) ? runTitle[..^": run".Length] : runTitle;

    /// <summary>The tool that lists devices with their CVEs (a device table with the Vulnerabilities column).</summary>
    public const string VulnerabilitiesTool = "oadm_vulnerabilities";

    /// <summary>
    /// The 4th column of a device table (docs/spec/assistant.md "What the panel shows"): the fact the question filtered by, as
    /// the device grid names its column, and the row field that holds it; null: the status.
    /// </summary>
    public static (string Header, string Field)? DetailOf(string tool, Proto.DeviceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (tool == VulnerabilitiesTool)
        {
            return ("Vulnerabilities", "vulnerabilities");
        }

        foreach (var field in DeviceTools.FieldsOf(filter))
        {
            switch (field)
            {
                case "vulnerabilities":
                    return ("Vulnerabilities", field);
                case "clock":
                    return ("Clock", field);
                case "certificate_expires_in":
                    return ("Certificate expires", field);
                case "soc":
                    return ("SoC", field);
            }
        }

        return null;
    }

    public static string NameOf(JsonObject row) =>
        new[] { Text(row["host_name"]), Text(row["upnp_name"]), Text(row["name"]), Text(row["address"]), Text(row["id"]) }.FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty;

    private static void Add(List<string> details, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            details.Add(Cut(label + ": " + value.ReplaceLineEndings(" ")));
        }
    }

    private static string Cut(string line) => line.Length <= MaxLine ? line : line[..(MaxLine - 1)] + "…";

    private static bool IsSecret(string name) => SecretWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>"new_version" to "New version".</summary>
    private static string Label(string name)
    {
        var words = name.Replace('_', ' ').Replace('-', ' ').Trim();
        return words.Length == 0 ? name : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? Names(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? string.Join(", ", list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()))
            : null;

    /// <summary>A plain value as text: a string, number or yes/no, a list of them joined; null for anything else.</summary>
    private static string? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Array when value.EnumerateArray().All(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number) =>
            string.Join(", ", value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())),
        _ => null,
    };

    private static string? Plain(JsonNode? value) => value is null ? null : Plain(JsonSerializer.SerializeToElement(value));

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    private static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
