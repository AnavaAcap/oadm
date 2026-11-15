using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Acap;

/// <summary>Constants shared by the server task and the client dialog.</summary>
public static class AcapPlugin
{
    public const string Id = "oadm.acap";

    /// <summary>VAPIX Application API (applications/list.cgi, upload.cgi, control.cgi, config.cgi) in apidiscovery.</summary>
    public const string ApplicationApiId = "application";

    public const string ApplicationApiMinVersion = "1.0";

    /// <summary>Read-only query: installed applications plus the device facts needed for compatibility checks.</summary>
    public const string ListApplicationsMethod = "listApplications";

    public static readonly JsonSerializerOptions Json = PluginJson.CreateOptions(PluginJsonEnums.CamelCase);
}

public enum AcapAction
{
    Install = 0,
    Remove = 1,
    Start = 2,
    Stop = 3,

    /// <summary>Install license keys for an installed application (key files only, matched by MAC address).</summary>
    InstallLicense = 4,

    /// <summary>Change the settings an installed application registered (<see cref="AcapPayload.Values"/>).</summary>
    Settings = 5,
}

/// <summary>One uploaded package of an install with several packages: one per architecture.</summary>
[Description("One uploaded package.")]
public sealed record AcapPackageFile(
    [property: Description("The file id the upload answered."), MinLength(1), MaxLength(64)] string FileId,
    [property: Description("The SHA-256 the upload answered."), MaxLength(64)] string? Sha256 = null,
    [property: Description("Several packages: the architecture of this one (aarch64, armv7hf, ...)."), MaxLength(32)] string? Architecture = null);

/// <summary>What the Applications task may do for AI agents; passwords and license keys are entered by the person.</summary>
public enum AcapMcpAction
{
    Install = 0,
    Remove = 1,
    Start = 2,
    Stop = 3,

    /// <summary>Install a license key the person enters (<see cref="AcapAction.InstallLicense"/>), for one device.</summary>
    InstallLicense = 4,

    /// <summary>Change settings (<see cref="AcapAction.Settings"/>); a password setting is refused.</summary>
    Settings = 5,
}

/// <summary>
/// The input of "Applications (ACAP)" for AI agents (docs/spec/mcp.md): install or upgrade from uploaded packages, remove,
/// start, stop, change settings and install a license key. A password setting goes in <see cref="Passwords"/> by name and a
/// license key is a secret: the person enters both (docs/spec/mcp.md "Secrets", <see cref="AcapTaskPlugin.SecretsOf"/>). This
/// record has no <see cref="AcapPayload.PasswordsAllowed"/>, so the task refuses a value in <see cref="Values"/> for a
/// setting the application declares as a password, and <see cref="AppSettings.OfflineProblems"/> refuses a name there that
/// points to a secret. The task reads the same text as an <see cref="AcapPayload"/>.
/// </summary>
[Description("Install, upgrade, remove, start or stop an application, change its settings, or install its license key.")]
public sealed record AcapMcpPayload(
    [property: Description("Install also upgrades. Settings changes values of one installed application.")] AcapMcpAction Action,
    [property: Description("Remove, Start, Stop and Settings: the package name of the application (as oadm_devices_get lists it). Install: leave it out."), MaxLength(64)] string? Application = null,
    [property: Description("Install: the uploaded .eap packages of one application: one, or several builds (per architecture and AXIS OS, also of other versions). Each device group gets one. Leave it out with zipFileId."), MaxLength(AcapPayload.MaxFiles)] IReadOnlyList<AcapPackageFile>? Files = null,
    [property: Description("Install: allow an older version than the installed one.")] bool AllowDowngrade = false,
    [property: Description("Install: start the application afterwards.")] bool StartAfterInstall = false,
    [property: Description("Settings: the new values by setting name, as text (at most 200). Each device checks them against the application's settings before anything is written. Passwords go in passwords.")] IReadOnlyDictionary<string, string>? Values = null,
    [property: Description("Settings: the password settings to set, by name; the user enters each value (at most 8)."), MaxLength(AcapPayload.MaxPasswords)] IReadOnlyList<AcapPasswordSetting>? Passwords = null,
    [property: Description("Install: the file id of one uploaded ZIP of a vendor with the .eap builds (also in subfolders) and an optional oadm-packages.json. Leave files out then."), MaxLength(64)] string? ZipFileId = null,
    [property: Description("Install with files: optional mapping rows like oadm-packages.json, each naming a file id of files. With zipFileId the ZIP's own oadm-packages.json applies."), MaxLength(AcapPackageRules.MaxRows)] IReadOnlyList<AcapPackageRule>? Rules = null,
    [property: Description("Install: the package of each device group the preview says OADM cannot choose (it names the groups and the candidates)."), MaxLength(AcapPayload.MaxMapping)] IReadOnlyList<AcapPackageMapping>? Mapping = null,
    [property: Oadm.Sdk.Tasks.Secret(Label = "license key")] string? LicenseKey = null);

/// <summary>A password setting of an agent's Settings change: its name; the value is the person's (<see cref="AcapTaskPlugin.SecretsOf"/>).</summary>
[Description("A password setting; the user enters its value.")]
public sealed record AcapPasswordSetting(
    [property: Description("The setting's name, as for values."), MinLength(1), MaxLength(128)] string Name,
    [property: Oadm.Sdk.Tasks.Secret] string? Value = null);

/// <summary>A refused payload with the JSON field it is about ("files", "zipFileId", "rules", "mapping").</summary>
public sealed class AcapPayloadException : ArgumentException
{
    public AcapPayloadException()
    {
    }

    public AcapPayloadException(string message)
        : base(message)
    {
    }

    public AcapPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AcapPayloadException(string field, string message)
        : base(message) => Field = field;

    /// <summary>The field, empty for the whole payload.</summary>
    public string Field { get; } = string.Empty;
}

/// <summary>Task payload produced by the dialog. Contains no passwords; license keys are kept in memory like every payload.</summary>
public sealed record AcapPayload
{
    public AcapAction Action { get; init; }

    /// <summary>Package name for remove/start/stop; for install the package name read in the dialog (cross-checked).</summary>
    public string? Application { get; init; }

    /// <summary>Install: id from <c>ITaskDialogContext.UploadAsync</c>.</summary>
    public string? FileId { get; init; }

    /// <summary>Install: SHA-256 of the uploaded file as reported by the upload (upper hex).</summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Install with several packages of the same application and version, one per architecture; each device gets the one of
    /// its architecture. Null or empty = the single <see cref="FileId"/> (older payloads).
    /// </summary>
    public IReadOnlyList<AcapPackageFile>? Files { get; init; }

    /// <summary>
    /// Install from one uploaded ZIP of a vendor instead of <see cref="Files"/> (agents, the Assistant): the task reads its .eap
    /// builds and its <c>oadm-packages.json</c> on the server (<see cref="EapZip"/>), never unpacked to disk.
    /// </summary>
    public string? ZipFileId { get; init; }

    /// <summary>Install with <see cref="Files"/>: the mapping rows (the dialog's from the ZIP's mapping file), each naming a file id.</summary>
    public IReadOnlyList<AcapPackageRule>? Rules { get; init; }

    /// <summary>Install: the build a person or an agent chose for a group OADM cannot decide (a file id, or an .eap name in the ZIP).</summary>
    public IReadOnlyList<AcapPackageMapping>? Mapping { get; init; }

    /// <summary>At most this many entries in <see cref="Mapping"/>.</summary>
    public const int MaxMapping = 1000;

    /// <summary>
    /// Settings: the new values by name below the application's group ("ONVIFEvents"). Kept in memory only like every
    /// payload; never in a log, a step text or the task name.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Values { get; init; }

    /// <summary>
    /// Settings of an agent: password settings by name whose values the person entered through the secret channel
    /// (<see cref="AcapTaskPlugin.SecretsOf"/>); written like <see cref="Values"/>, these names also when they are passwords.
    /// </summary>
    public IReadOnlyList<AcapPasswordSetting>? Passwords { get; init; }

    /// <summary>InstallLicense of an agent: the text of one license key file the person entered; added to <see cref="LicenseKeys"/> by <see cref="Parse(string?, bool)"/>.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("licenseKey")]
    public string? AgentLicenseKey { get; init; }

    /// <summary>Most password settings of one agent's change (the secret channel's slots of one preview).</summary>
    public const int MaxPasswords = 8;

    public const string EnterPassword = "Enter the password.";
    public const string EnterLicenseKey = "Enter the license key.";
    public const string BadLicenseKey = "This is not a license key file.";

    /// <summary>The values to write: <see cref="Values"/> and the entered <see cref="Passwords"/>.</summary>
    public IReadOnlyDictionary<string, string> AllValues()
    {
        var all = new Dictionary<string, string>(Values ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var password in Passwords ?? [])
        {
            all[password.Name] = password.Value ?? string.Empty;
        }

        return all;
    }

    /// <summary>The names of <see cref="Passwords"/>: the person entered their values, so they may be passwords.</summary>
    public IReadOnlySet<string> EnteredPasswords() => (Passwords ?? []).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Settings: a person typed the values in OADM's Change settings dialog, so password settings may be among them. False (the
    /// default, and always for AI agents: <see cref="AcapMcpPayload"/> has no such field, and the tools refuse unknown fields)
    /// refuses a value for a setting the application declares as a password, and a name that points to a secret, before
    /// anything is written.
    /// </summary>
    public bool PasswordsAllowed { get; init; }

    /// <summary>At most this many packages in one install.</summary>
    public const int MaxFiles = 16;

    /// <summary>The packages of an install: <see cref="Files"/>, else the single <see cref="FileId"/>.</summary>
    public IReadOnlyList<AcapPackageFile> AllFiles() =>
        Files is { Count: > 0 } files ? files
        : string.IsNullOrWhiteSpace(FileId) ? []
        : [new AcapPackageFile(FileId, Sha256)];

    /// <summary>Install: package version read in the dialog (cross-checked against the server-side read).</summary>
    public string? Version { get; init; }

    /// <summary>Install: permit installing a lower version than the installed one.</summary>
    public bool AllowDowngrade { get; init; }

    /// <summary>Install: start the application after a successful install.</summary>
    public bool StartAfterInstall { get; init; }

    /// <summary>
    /// Install (optional) and InstallLicense: the license keys of the selected devices, one per device. Each task takes the key
    /// with its device's MAC address and parses it again; a device without one is Done with warnings.
    /// </summary>
    public IReadOnlyList<LicenseKeyEntry> LicenseKeys { get; init; } = [];

    /// <summary>
    /// The application id the keys are for (the package's or the chosen application's), used when the device does not report
    /// an id for the installed application; null = not known.
    /// </summary>
    public string? LicenseApplicationId { get; init; }

    /// <summary>Friendly name of the application ("AXIS Video Motion Detection"); only names the task.</summary>
    public string? AppDisplayName { get; init; }

    /// <summary>Install: the kind on every compatible device in the dialog preview (null = mixed); only names the task.</summary>
    public InstallKind? Kind { get; init; }

    /// <summary>
    /// The task name: "Install AXIS Video Motion Detection 4.5.2", "Upgrade ... to 4.5.2" (every device
    /// upgrades), "Remove ...", "Start ...", "Stop ...".
    /// </summary>
    public string TaskName()
    {
        var app = !string.IsNullOrWhiteSpace(AppDisplayName) ? AppDisplayName.Trim()
            : !string.IsNullOrWhiteSpace(Application) ? Application.Trim()
            : "application";
        var version = string.IsNullOrWhiteSpace(Version) ? null : Version.Trim();
        return Action switch
        {
            AcapAction.Install when Kind == InstallKind.Upgrade && version is not null => $"Upgrade {app} to {version}{WithLicense}",
            AcapAction.Install => (version is null ? $"Install {app}" : $"Install {app} {version}") + WithLicense,
            AcapAction.InstallLicense => $"Install license of {app}",
            AcapAction.Settings => $"Change settings of {app}",
            AcapAction.Remove => $"Remove {app}",
            AcapAction.Start => $"Start {app}",
            _ => $"Stop {app}",
        };
    }

    private string WithLicense => LicenseKeys.Count > 0 ? " with license" : string.Empty;

    public string ToJson() => JsonSerializer.Serialize(this, AcapPlugin.Json);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, AcapPayload> Parsed = new();

    /// <summary>
    /// <see cref="Parse"/> once per payload string: the engine hands the same string to every task of a run, so 5,000 tasks
    /// with 5,000 license keys parse it once, not 5,000 times.
    /// </summary>
    public static AcapPayload ParseShared(string? json)
    {
        if (json is null)
        {
            return Parse(json);
        }

        if (Parsed.TryGetValue(json, out var cached))
        {
            return cached;
        }

        var payload = Parse(json);
        Parsed.AddOrUpdate(json, payload);
        return payload;
    }

    private Dictionary<string, List<LicenseKeyEntry>>? _keysByMac;

    /// <summary>The first key of the device with this MAC address (normalized), null without one.</summary>
    public LicenseKeyEntry? KeyFor(string mac) => KeysFor(mac) is { Count: > 0 } keys ? keys[0] : null;

    /// <summary>Every key of the device with this MAC address (normalized; one per version range). Indexed once per payload.</summary>
    public IReadOnlyList<LicenseKeyEntry> KeysFor(string mac)
    {
        var index = _keysByMac;
        if (index is null)
        {
            index = new Dictionary<string, List<LicenseKeyEntry>>(StringComparer.Ordinal);
            foreach (var key in LicenseKeys)
            {
                if (LicenseKey.NormalizeMac(key.DeviceId) is { } normalized)
                {
                    if (!index.TryGetValue(normalized, out var list))
                    {
                        index[normalized] = list = [];
                    }

                    list.Add(key);
                }
            }

            _keysByMac = index;
        }

        return index.TryGetValue(mac, out var keys) ? keys : [];
    }

    /// <summary>Parses and validates a payload. Throws <see cref="ArgumentException"/> for invalid input; nothing has been changed then.</summary>
    public static AcapPayload Parse(string? json) => Parse(json, requireSecrets: true);

    /// <summary>
    /// <see cref="Parse(string?)"/>; with <paramref name="requireSecrets"/> false an agent's password and license key may still be
    /// empty (the plan of a preview, before the person entered them).
    /// </summary>
    public static AcapPayload Parse(string? json, bool requireSecrets)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The task needs a payload from the Applications dialog. Nothing was changed.", nameof(json));
        }

        AcapPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<AcapPayload>(json, AcapPlugin.Json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The task payload is not valid. Nothing was changed.", nameof(json), ex);
        }

        if (payload is null || !Enum.IsDefined(payload.Action))
        {
            throw new ArgumentException("The task payload has no valid action. Nothing was changed.", nameof(json));
        }

        payload = payload with { LicenseKeys = payload.LicenseKeys ?? [] };
        if (!string.IsNullOrEmpty(payload.AgentLicenseKey))
        {
            LicenseKey key;
            try
            {
                key = LicenseKey.Parse(payload.AgentLicenseKey);
            }
            catch (FormatException)
            {
                throw new ArgumentException(BadLicenseKey + " Nothing was changed.", nameof(json));
            }

            payload = payload with { LicenseKeys = [.. payload.LicenseKeys, new LicenseKeyEntry(key.DeviceId, payload.AgentLicenseKey)], LicenseApplicationId = payload.LicenseApplicationId ?? key.ApplicationId };
        }

        if (payload.Passwords is { Count: > 0 } passwords)
        {
            if (payload.Action != AcapAction.Settings || passwords.Count > MaxPasswords
                || passwords.Any(p => p is null || !AppSettings.IsValidName(p.Name))
                || passwords.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != passwords.Count
                || passwords.Any(p => payload.Values?.ContainsKey(p.Name) == true))
            {
                throw new ArgumentException("The password settings of the task are not valid. Nothing was changed.", nameof(json));
            }

            if (requireSecrets && passwords.Any(p => string.IsNullOrEmpty(p.Value)))
            {
                throw new ArgumentException(EnterPassword + " Nothing was changed.", nameof(json));
            }
        }

        if (payload.Action == AcapAction.Install)
        {
            ValidateFiles(payload);
        }
        else if (!EapReader.IsValidAppName(payload.Application))
        {
            throw new ArgumentException("The application name is not valid. Nothing was changed.", nameof(json));
        }

        if (payload.Action == AcapAction.InstallLicense && payload.LicenseKeys.Count == 0 && requireSecrets)
        {
            throw new ArgumentException("Choose the license key files. Nothing was changed.", nameof(json));
        }

        if (payload.LicenseKeys.Count > LicenseKeySet.MaxKeys
            || payload.LicenseKeys.Any(k => k is null || LicenseKey.NormalizeMac(k.DeviceId) is null || string.IsNullOrEmpty(k.Xml) || k.Xml.Length > LicenseKey.MaxBytes))
        {
            throw new ArgumentException("The license keys of the task are not valid. Nothing was changed.", nameof(json));
        }

        if (payload.Action == AcapAction.Settings)
        {
            if (payload.AllValues() is not { Count: > 0 } values)
            {
                throw new ArgumentException("Choose at least one setting to change. Nothing was changed.", nameof(json));
            }

            if (values.Count > AppSettings.MaxValues
                || values.Any(v => !AppSettings.IsValidName(v.Key) || v.Value is null || v.Value.Length > AppSettings.MaxValueLength))
            {
                throw new ArgumentException("The settings of the task are not valid. Nothing was changed.", nameof(json));
            }
        }

        return payload;
    }

    /// <summary>
    /// An install names one package or several builds of one application (16 at most, each file once), or one ZIP; the mapping
    /// rows and the choices name files of the install and are bounded.
    /// </summary>
    private static void ValidateFiles(AcapPayload payload)
    {
        var files = payload.AllFiles();
        var zip = !string.IsNullOrWhiteSpace(payload.ZipFileId);
        if (zip && files.Count > 0)
        {
            throw new AcapPayloadException("zipFileId", "Give either files or zipFileId, not both. Nothing was changed.");
        }

        if (!zip && (files.Count == 0 || files.Any(f => f is null || string.IsNullOrWhiteSpace(f.FileId))))
        {
            throw new AcapPayloadException("files", "Install needs an uploaded package. Nothing was changed.");
        }

        if (files.Count > MaxFiles || files.Select(f => f.FileId).Distinct(StringComparer.Ordinal).Count() != files.Count)
        {
            throw new AcapPayloadException("files", string.Create(CultureInfo.InvariantCulture, $"The packages of the task are not valid: at most {MaxFiles} files, each once. Nothing was changed."));
        }

        var ids = files.Select(f => f.FileId).ToHashSet(StringComparer.Ordinal);
        if (payload.Rules is { } rules)
        {
            if (zip)
            {
                throw new AcapPayloadException("rules", "Leave rules out with zipFileId: the mapping file in the ZIP applies. Nothing was changed.");
            }

            if (rules.Count > AcapPackageRules.MaxRows || rules.Any(r => r is null || !ids.Contains(r.File ?? string.Empty)
                || r.Models?.Count > AcapPackageRules.MaxValues || r.Soc?.Count > AcapPackageRules.MaxValues))
            {
                throw new AcapPayloadException("rules", "Each mapping row names a file id of files. Nothing was changed.");
            }
        }

        if (payload.Mapping is { } mapping)
        {
            if (mapping.Count > MaxMapping || mapping.Any(m => m is null || string.IsNullOrWhiteSpace(m.AxisOs) || string.IsNullOrWhiteSpace(m.Architecture)
                || string.IsNullOrWhiteSpace(m.File) || (!zip && !ids.Contains(m.File))))
            {
                throw new AcapPayloadException("mapping", "Each choice names the AXIS OS, the architecture and a package of this install. Nothing was changed.");
            }
        }
    }
}

/// <summary>Device facts needed for the compatibility checks.</summary>
public sealed record AcapDeviceFacts
{
    /// <summary>basicdeviceinfo Architecture, e.g. aarch64 or armv7hf.</summary>
    public string? Architecture { get; init; }

    public string? FirmwareVersion { get; init; }

    /// <summary>Properties.EmbeddedDevelopment.Version, e.g. 2.18.</summary>
    public string? EmbeddedDevelopmentVersion { get; init; }

    /// <summary>config.cgi AllowUnsigned; null when the device does not offer it (before AXIS OS 11.2).</summary>
    public bool? AllowUnsigned { get; init; }

    /// <summary>basicdeviceinfo ProdNbr ("P3265-V"); null when not read.</summary>
    public string? Model { get; init; }

    /// <summary>basicdeviceinfo ProdShortName ("AXIS P3265-V"); null when not read.</summary>
    public string? ShortName { get; init; }

    /// <summary>basicdeviceinfo Soc ("Axis Artpec-8"); null when not read.</summary>
    public string? Soc { get; init; }
}

/// <summary>One entry of applications/list.cgi.</summary>
public sealed record InstalledApplication
{
    public required string Name { get; init; }

    public string? NiceName { get; init; }

    public string? Vendor { get; init; }

    public string? Version { get; init; }

    /// <summary>Running, Stopped or Idle.</summary>
    public string? Status { get; init; }

    /// <summary>Valid, Invalid, Missing, Custom or None.</summary>
    public string? License { get; init; }

    public string? LicenseExpirationDate { get; init; }

    public string? ApplicationId { get; init; }

    public bool Bundled { get; init; }

    /// <summary>Signed, Unsigned or Unknown.</summary>
    public string? SignatureStatus { get; init; }

    public IReadOnlyList<OsVersionRange> CompatibleOsVersions { get; init; } = [];

    /// <summary>The application's own page as list.cgi names it ("local/ax_msf/redirect.html"); null when it has none.</summary>
    public string? ConfigurationPage { get; init; }

    /// <summary>
    /// <see cref="ConfigurationPage"/> as a path on the device ("/local/ax_msf/redirect.html"); null without one or when it
    /// would leave the device (<see cref="Oadm.Sdk.Vapix.DevicePaths.ToDevicePath"/>).
    /// </summary>
    [JsonIgnore]
    public string? ConfigurationPath => Oadm.Sdk.Vapix.DevicePaths.ToDevicePath(ConfigurationPage);

    [JsonIgnore]
    public bool IsRunning => string.Equals(Status, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>The device says the application needs no license (<c>License="None"</c>, e.g. AXIS Object Analytics).</summary>
    [JsonIgnore]
    public bool NeedsNoLicense => string.Equals(License, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Valid until 2027-09-18", "Valid", "Not required" (None), "Missing", "Invalid", "Unknown".</summary>
    [JsonIgnore]
    public string LicenseText => License switch
    {
        null or "" => "Unknown",
        "Valid" when LicenseExpirationDate is { Length: > 0 } d && d != "0" => $"Valid until {d}",
        _ when NeedsNoLicense => "Not required",
        var l => l,
    };

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(NiceName) ? Name : NiceName;
}

/// <summary>Result of the <see cref="AcapPlugin.ListApplicationsMethod"/> query.</summary>
public sealed record ListApplicationsResult
{
    public AcapDeviceFacts Device { get; init; } = new();

    public IReadOnlyList<InstalledApplication> Applications { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, AcapPlugin.Json);

    public static ListApplicationsResult FromJson(string json) =>
        JsonSerializer.Deserialize<ListApplicationsResult>(json, AcapPlugin.Json)
        ?? throw new JsonException("Empty listApplications result.");
}
