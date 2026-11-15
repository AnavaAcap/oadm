using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// An agent's run of Create configuration file (<see cref="CreateConfigFileMcpPayload"/>): which parameters (exact names or
/// groups ending in a dot), action rules and stream profiles of a device go into its file. Passwords and other secret values
/// never do (<see cref="SecretNames"/>).
/// </summary>
public sealed class CreateConfigFileRequest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonSerializerOptions JsonOptions => Options;

    /// <summary>The dialog's text when nothing is checked.</summary>
    public const string NothingChosen = "Check what to save";

    public const string NoSecrets = "Passwords and other secret values are never saved.";

    public const string BadName = "Enter a name, each once.";

    private CreateConfigFileRequest(IReadOnlyList<string> parameters, IReadOnlyList<string> rules, IReadOnlyList<string> profiles)
    {
        Parameters = parameters;
        Rules = rules;
        Profiles = profiles;
    }

    public IReadOnlyList<string> Parameters { get; }

    public IReadOnlyList<string> Rules { get; }

    public IReadOnlyList<string> Profiles { get; }

    /// <summary>The request and its problems, each at its JSON field (<c>parameters[1]</c>). Reads only the payload.</summary>
    public static (CreateConfigFileRequest? Request, IReadOnlyList<PayloadError> Errors) Check(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, [new(string.Empty, NothingChosen)]);
        }

        CreateConfigFileMcpPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<CreateConfigFileMcpPayload>(json, Options);
        }
        catch (JsonException)
        {
            return (null, [new(string.Empty, NothingChosen)]);
        }

        var parameters = payload?.Parameters ?? [];
        var rules = payload?.ActionRules ?? [];
        var profiles = payload?.StreamProfiles ?? [];
        if (parameters.Count + rules.Count + profiles.Count == 0)
        {
            return (null, [new(string.Empty, NothingChosen)]);
        }

        var errors = new List<PayloadError>();
        Names(parameters, "parameters", ConfigureIds.MaxParameters, ConfigureMcpSetting.MaxNameLength, errors, secretCheck: true);
        Names(rules, "actionRules", ConfigureIds.MaxRules, ConfigurePayload.MaxTextLength, errors, secretCheck: false);
        Names(profiles, "streamProfiles", ConfigureIds.MaxStreamProfiles, ConfigureIds.MaxStreamProfileName, errors, secretCheck: false);
        return errors.Count > 0
            ? (null, errors)
            : (new CreateConfigFileRequest([.. parameters.Select(p => p.Trim())], [.. rules], [.. profiles]), []);
    }

    private static void Names(IReadOnlyList<string> names, string field, int max, int maxLength, List<PayloadError> errors, bool secretCheck)
    {
        if (names.Count > max)
        {
            errors.Add(new(field, ConfigurePayload.TooMany));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var at = string.Create(CultureInfo.InvariantCulture, $"{field}[{i}]");
            var name = names[i];
            if (string.IsNullOrWhiteSpace(name) || name.Length > maxLength || name.Any(char.IsControl) || !seen.Add(name.Trim()))
            {
                errors.Add(new(at, BadName));
            }
            else if (secretCheck && SecretNames.IsSecret(name))
            {
                errors.Add(new(at, NoSecrets));
            }
        }
    }

    /// <summary>What a device's read gives for this request, without secrets; <see cref="Chosen.Missing"/> names what it lacks.</summary>
    public Chosen Choose(DeviceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var missing = new List<string>();
        var parameters = new List<NameValue>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in Parameters)
        {
            var group = name.EndsWith('.');
            var matches = settings.Parameters
                .Where(d => group ? d.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) : string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                .Where(d => !SecretNames.IsSecret(d) && !DangerousParameters.IsAddress(d.Name))
                .ToList();
            if (matches.Count == 0)
            {
                missing.Add(name);
            }

            parameters.AddRange(matches.Where(d => taken.Add(d.Name)).Select(d => new NameValue(d.Name, d.Value ?? string.Empty)));
        }

        var rules = new List<ConfigActionRule>();
        foreach (var name in Rules)
        {
            var found = settings.Rules.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).ToList();
            if (found.Count == 0)
            {
                missing.Add("action rule " + name);
            }

            // A recipient's password in an action never goes into the file of an agent's run.
            rules.AddRange(found.Select(r => r with
            {
                Action = r.Action with { Parameters = [.. r.Action.Parameters.Select(p => SecretNames.IsSecret(p.Name) ? p with { Value = string.Empty } : p)] },
            }));
        }

        var profiles = new List<ConfigStreamProfile>();
        foreach (var name in Profiles)
        {
            var found = settings.StreamProfiles.Where(p => string.Equals(p.Name, name, StringComparison.Ordinal)).ToList();
            if (found.Count == 0)
            {
                missing.Add("stream profile " + name);
            }

            profiles.AddRange(found);
        }

        return new Chosen(parameters, rules, profiles, missing);
    }

    /// <summary>The settings of one device that go into its file, and the names it does not have.</summary>
    public sealed record Chosen(IReadOnlyList<NameValue> Parameters, IReadOnlyList<ConfigActionRule> Rules, IReadOnlyList<ConfigStreamProfile> Profiles, IReadOnlyList<string> Missing)
    {
        public bool IsEmpty => Parameters.Count == 0 && Rules.Count == 0 && Profiles.Count == 0;
    }
}
