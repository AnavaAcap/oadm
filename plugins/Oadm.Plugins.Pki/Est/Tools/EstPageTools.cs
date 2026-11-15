using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Oadm.Plugins.Pki.Servers;
using Oadm.Sdk.Licensing;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Tools;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Page actions", decision 2026-11-15): Turn on and Turn off, the Server card's
/// Save, a profile's save and delete, and Delete all of the Enrollments tab, each a preview (the page's checks, nothing written)
/// and a run with the rights of its page method (Administrator, "manage", users with all devices, audited).
/// </summary>
internal static class EstPageTools
{
    public const string NoEnrollments = "The enrollments list is empty.";
    public const string Off = "The EST server is off. Turn it on first.";
    public const string AlreadyOn = "The EST server is on already.";
    public const string AlreadyOff = "The EST server is off already.";
    public const string NoProfile = "No profile has this name.";
    public const string ChooseUse = "Use https, dot1x, mqtt or rtsps.";

    public static PluginPageTools<EstPlugin> Set { get; } = new PluginPageTools<EstPlugin>(EstTools.Json)
        .Change<NoArgs, ClearPlan, ClearResult>(
            new("enrollments_clear", "Delete all enrollments", "Deletes every entry of the EST server's enrollments list. Issued certificates stay valid.")
            {
                Destructive = true,
            },
            EstMethods.ClearEnrollments,
            (plugin, _, _, _) =>
            {
                var count = Running(plugin).Enrollments(null).Count;
                return count == 0 ? throw new InvalidOperationException(NoEnrollments) : Task.FromResult(new ClearPlan(count));
            },
            (plugin, _, _, plan, _) =>
            {
                Running(plugin).ClearEnrollments();
                return Task.FromResult(new ClearResult(plan.WillChange));
            })
        .Change<EnableArgs, EnablePlan, EnableResult>(
            new("enable", "Turn the EST server on or off", "Turns the EST server on (needs OADM PRO) or off. Off closes its port: cameras cannot get or renew certificates over EST."),
            ServerSwitchMethods.TurnOn,
            (plugin, _, input, _) => Task.FromResult(CheckSwitch(Switch(plugin), input.Args.Enabled)),
            async (plugin, _, input, _, ct) =>
            {
                var server = Switch(plugin);
                var state = input.Args.Enabled ? await server.TurnOnAsync(ct).ConfigureAwait(false) : await server.TurnOffAsync(ct).ConfigureAwait(false);
                return new EnableResult(state.On, state.Running, state.StatusText);
            })
        .Change<SettingsArgs, SettingsPlan, SettingsResult>(
            new("settings_save", "Save EST server settings", "Changes the EST server's port, the interface it listens on and how many enrollments it keeps. Fields left out keep their value."),
            EstMethods.Save,
            (plugin, _, input, _) => Task.FromResult(CheckSettings(Running(plugin), input.Args)),
            async (plugin, _, _, plan, ct) => new SettingsResult(await SaveAsync(Running(plugin), plan.Config, ct).ConfigureAwait(false)))
        .Change<ProfileArgs, ProfilePlan, SettingsResult>(
            new("profile_save", "Save EST profile", "Adds an EST profile or changes the one with this name: its use, its issuing policy and whether cameras may use it."),
            EstMethods.Save,
            (plugin, _, input, _) => Task.FromResult(CheckProfile(Running(plugin), input.Args)),
            async (plugin, _, _, plan, ct) => new SettingsResult(await SaveAsync(Running(plugin), plan.Config, ct).ConfigureAwait(false)))
        .Change<ProfileRef, ProfilePlan, SettingsResult>(
            new("profile_delete", "Delete EST profile", "Deletes an EST profile. Cameras set up with it can no longer enroll or renew there.")
            {
                Destructive = true,
            },
            EstMethods.Save,
            (plugin, _, input, _) => Task.FromResult(CheckDelete(Running(plugin), input.Args)),
            async (plugin, _, _, plan, ct) => new SettingsResult(await SaveAsync(Running(plugin), plan.Config, ct).ConfigureAwait(false)));

    private static Serving.EstService Running(EstPlugin plugin) => plugin.Service ?? throw new InvalidOperationException(plugin.Server is null ? EstPlugin.NotRunning : Off);

    private static ServerSwitch<Serving.EstService> Switch(EstPlugin plugin) => plugin.Server ?? throw new InvalidOperationException(EstPlugin.NotRunning);

    /// <summary>The page method whose audit entry a tool's run writes, as the page's own call does (Turn off for an off run).</summary>
    public static string? AuditDetail(string method, string? payloadJson)
    {
        if (Set.ToolOf(method) is not { } tool || PluginTool.IsPreviewMethod(method))
        {
            return null;
        }

        return tool.Name == "enable" && !EnabledOf(payloadJson) ? ServerSwitchMethods.TurnOff : Set.PageMethodOf(method);
    }

    private static bool EnabledOf(string? payloadJson)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(payloadJson ?? "{}")?["enabled"]?.GetValue<bool>() == true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static EnablePlan CheckSwitch(ServerSwitch<Serving.EstService> server, bool enabled)
    {
        var state = server.State;
        if (enabled)
        {
            if (!state.ProActive)
            {
                throw new InvalidOperationException(ProLicenseTexts.NeedsPro);
            }

            return state.On ? throw new InvalidOperationException(AlreadyOn)
                : new EnablePlan(true, state.StatusText, ["The server opens its port and cameras that are set up get and renew certificates over EST."]);
        }

        return !state.On ? throw new InvalidOperationException(AlreadyOff)
            : new EnablePlan(false, state.StatusText, ["The port closes. Cameras cannot get or renew certificates over EST until it is on again."]);
    }

    private static SettingsPlan CheckSettings(Serving.EstService service, SettingsArgs args)
    {
        var now = service.Store.Config;
        var config = now with
        {
            Port = args.Port ?? now.Port,
            InterfaceId = args.InterfaceId ?? now.InterfaceId,
            EnrollmentsKept = args.EnrollmentsKept ?? now.EnrollmentsKept,
        };
        Check(service, config, field => field switch { "interface" => "interface_id", "enrollmentsKept" => "enrollments_kept", _ => field });
        var warnings = new List<string>();
        if (config.Port != now.Port || !string.Equals(config.InterfaceId, now.InterfaceId, StringComparison.Ordinal))
        {
            warnings.Add("Cameras set up with the old address or port cannot reach the server until they are set up again (Certificate enrollment).");
        }

        return new SettingsPlan(config.Port, config.InterfaceId, config.EnrollmentsKept, warnings) { Config = config };
    }

    private static ProfilePlan CheckProfile(Serving.EstService service, ProfileArgs args)
    {
        var now = service.Store.Config;
        var index = now.Profiles.ToList().FindIndex(p => string.Equals(p.Name, args.Name, StringComparison.OrdinalIgnoreCase));
        var before = index >= 0 ? now.Profiles[index] : null;
        ProfileUse use;
        if (args.Use is { } useText)
        {
            use = useText switch
            {
                "https" => ProfileUse.Https,
                "dot1x" => ProfileUse.Dot1x,
                "mqtt" => ProfileUse.Mqtt,
                "rtsps" => ProfileUse.Rtsps,
                _ => throw new PluginToolInputException("use", ChooseUse),
            };
        }
        else
        {
            use = before?.Use ?? throw new PluginToolInputException("use", ChooseUse);
        }

        Guid? policy = before?.PolicyId;
        if (args.PolicyId is { Length: > 0 } policyText)
        {
            policy = Guid.TryParse(policyText, out var id) ? id : throw new PluginToolInputException("policy_id", EstTexts.ChoosePolicy);
        }

        var profile = (before ?? new EstProfile(args.Name ?? string.Empty, use)) with { Use = use, Enabled = args.Enabled ?? before?.Enabled ?? true, PolicyId = policy };
        var profiles = now.Profiles.ToList();
        if (index >= 0)
        {
            profiles[index] = profile;
        }
        else
        {
            profiles.Add(profile);
        }

        var config = now with { Profiles = profiles };
        Check(service, config, ProfileField);
        var warnings = before is not null && before.Use != use ? ["Cameras set up with this profile get the new use's certificates at their next renewal."] : Array.Empty<string>();
        return new ProfilePlan(before is null ? "create" : "change", profile.Name, UseText(use), profile.Enabled, warnings) { Config = config };
    }

    private static ProfilePlan CheckDelete(Serving.EstService service, ProfileRef args)
    {
        var now = service.Store.Config;
        var profile = now.Profiles.FirstOrDefault(p => string.Equals(p.Name, args.Name, StringComparison.OrdinalIgnoreCase))
            ?? throw new PluginToolInputException("name", NoProfile);
        var config = now with { Profiles = [.. now.Profiles.Where(p => !ReferenceEquals(p, profile))] };
        Check(service, config, field => field == "profiles" ? "name" : field);
        return new ProfilePlan("delete", profile.Name, UseText(profile.Use), profile.Enabled,
            ["Cameras set up with this profile can no longer enroll or renew their certificate there."]) { Config = config };
    }

    private static void Check(Serving.EstService service, EstConfig config, Func<string, string> field)
    {
        if (service.SaveRefusal(config) is { } error)
        {
            throw new PluginToolInputException(field(error.Field), error.Message);
        }
    }

    /// <summary>"profiles[2].policy" to "policy_id", "profiles[2].name" to "name".</summary>
    private static string ProfileField(string field) =>
        field.EndsWith(".policy", StringComparison.Ordinal) ? "policy_id"
        : field.EndsWith(".use", StringComparison.Ordinal) ? "use"
        : field.EndsWith(".validityDays", StringComparison.Ordinal) ? "policy_id"
        : "name";

    private static string UseText(ProfileUse use) => use switch
    {
        ProfileUse.Dot1x => "dot1x",
        ProfileUse.Mqtt => "mqtt",
        ProfileUse.Rtsps => "rtsps",
        _ => "https",
    };

    private static async Task<string> SaveAsync(Serving.EstService service, EstConfig config, CancellationToken ct)
    {
        var reply = await service.SaveAsync(new SaveRequest(config), ct).ConfigureAwait(false);
        return reply.Error is { } error ? throw new PluginToolInputException(reply.Field ?? string.Empty, error) : reply.State?.StatusText ?? string.Empty;
    }

    internal sealed record NoArgs;

    internal sealed record ClearPlan(int Entries) : PluginToolPlan(Entries, "enrollment");

    internal sealed record ClearResult(int Deleted);

    internal sealed record EnableArgs([property: Description("true turns the server on, false off.")] bool Enabled);

    internal sealed record EnablePlan(
        [property: Description("The server is on after the run.")] bool Enabled,
        [property: Description("The state now.")] string StateNow,
        IReadOnlyList<string> Warnings) : PluginToolPlan(1, "server");

    internal sealed record EnableResult(bool On, bool Running, [property: Description("Why it does not run; empty while it runs.")] string Status);

    internal sealed record SettingsArgs(
        [property: Description("The TCP port, 1025 to 65535 (default 9454)."), Range(1025, 65535)] int? Port = null,
        [property: Description("The interface id it listens on, or all."), MaxLength(200)] string? InterfaceId = null,
        [property: Description("Enrollments kept, 100 to 5,000."), Range(100, 5000)] int? EnrollmentsKept = null);

    internal sealed record SettingsPlan(int Port, string InterfaceId, int EnrollmentsKept, IReadOnlyList<string> Warnings) : PluginToolPlan(1, "setting")
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public EstConfig Config { get; init; } = new();
    }

    internal sealed record SettingsResult([property: Description("The server's state after the save.")] string Status);

    internal sealed record ProfileArgs(
        [property: Description("The profile's name: 1 to 32 letters, digits or _ (the path the cameras use)."), MaxLength(32)] string? Name,
        [property: Description("https, dot1x, mqtt or rtsps; one profile per use. Needed for a new profile."), MaxLength(8)] string? Use = null,
        [property: Description("The issuing policy of PKI (oadm_pki_policies) its certificates are issued with."), MaxLength(36)] string? PolicyId = null,
        [property: Description("Cameras may use it (default true).")] bool? Enabled = null);

    internal sealed record ProfileRef([property: Description("The profile's name."), MaxLength(32)] string? Name);

    internal sealed record ProfilePlan(
        [property: Description("create, change or delete.")] string Change,
        string Name,
        string Use,
        bool Enabled,
        IReadOnlyList<string> Warnings) : PluginToolPlan(1, "profile")
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public EstConfig Config { get; init; } = new();
    }
}
