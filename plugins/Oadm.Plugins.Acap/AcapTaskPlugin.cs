using System.Globalization;

using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Formatting;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Acap;

/// <summary>
/// "Applications (ACAP)..." task: install/upgrade an .eap, remove, start or stop an application on
/// the selected devices. Follows the device safety rule: API check against the cached list in
/// <see cref="CanRun"/>, fresh <c>Require</c> plus package/device validation before the first write.
/// </summary>
#if OADM_EMBEDDED_ACAP
// A narrow bundled task can reuse this execution code without registering the general ACAP task in its package.
internal sealed class AcapTaskPlugin : ITaskPlugin, ITaskPluginQuery
#else
public sealed class AcapTaskPlugin : ITaskPlugin, ITaskPluginQuery
#endif
{
    public static readonly TimeSpan DefaultVerifyInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultVerifyTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _verifyInterval;
    private readonly TimeSpan _verifyTimeout;
    private readonly TimeProvider _time;
    private readonly Func<IVapixClient, AcapAction, CancellationToken, Task>? _beforeWrite;

    public AcapTaskPlugin()
        : this(DefaultVerifyInterval, DefaultVerifyTimeout, TimeProvider.System)
    {
    }

    /// <param name="verifyInterval">Poll interval when the upload answer was lost and the install is verified via list.cgi.</param>
    /// <param name="verifyTimeout">How long to keep verifying.</param>
    public AcapTaskPlugin(TimeSpan verifyInterval, TimeSpan verifyTimeout, TimeProvider timeProvider)
        : this(verifyInterval, verifyTimeout, timeProvider, null)
    {
    }

    /// <summary>Execution-specific authorization recheck; never mutate a shared task instance between camera writes.</summary>
    public AcapTaskPlugin(TimeSpan verifyInterval, TimeSpan verifyTimeout, TimeProvider timeProvider, Func<IVapixClient, AcapAction, CancellationToken, Task>? beforeWrite)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(verifyInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(verifyTimeout, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _verifyInterval = verifyInterval;
        _verifyTimeout = verifyTimeout;
        _time = timeProvider;
        _beforeWrite = beforeWrite;
    }

    public string Id => AcapPlugin.Id;

    public string DisplayName => "Applications (ACAP)";

    public string Group => TaskGroups.Applications;

    public string? IconKey => "plugin";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>Installing, downgrading, starting and stopping, and removing applications are rights of their own.</summary>
    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(AcapPermissions.Install, "Install and upgrade applications", "Install applications on devices or upgrade them."),
        new(AcapPermissions.Downgrade, "Downgrade applications", "Install an older version of an application.") { OperatorDefault = false },
        new(AcapPermissions.StartStop, "Start and stop applications", "Start and stop applications that are installed."),
        new(AcapPermissions.Remove, "Remove applications", "Delete applications from devices; their settings are lost.") { OperatorDefault = false },
        new(AcapPermissions.License, "Install license keys", "Install license key files for applications."),
        new(AcapPermissions.Settings, "Change application settings", "Change the settings applications register on devices."),
    ];

    /// <summary>The permission of the run's action; an install with "Allow downgrade" also needs "downgrade".</summary>
    public IReadOnlyList<string>? RequiredPermissions(string? payloadJson)
    {
        var payload = AcapPayload.Parse(payloadJson, requireSecrets: false); // an agent's preview: before the person entered them
        var license = payload.LicenseKeys.Count > 0 ? new[] { AcapPermissions.License } : [];
        return payload.Action switch
        {
            AcapAction.Install when payload.AllowDowngrade => [AcapPermissions.Install, AcapPermissions.Downgrade, .. license],
            AcapAction.Install => [AcapPermissions.Install, .. license],
            AcapAction.InstallLicense => [AcapPermissions.License],
            AcapAction.Start or AcapAction.Stop => [AcapPermissions.StartStop],
            AcapAction.Remove => [AcapPermissions.Remove],
            AcapAction.Settings => [AcapPermissions.Settings],
            _ => null,
        };
    }

    /// <summary>"Install AXIS Video Motion Detection 4.5.2", "Upgrade ... to ...", "Remove ...", "Start ...", "Stop ...".</summary>
    public string GetTaskName(string? payloadJson)
    {
        try
        {
            return AcapPayload.Parse(payloadJson, requireSecrets: false).TaskName();
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }
    }

    private static readonly Version MinApplicationApi = Version.Parse(AcapPlugin.ApplicationApiMinVersion);

    /// <summary>Reachable with credentials, and the cached API list offers the Application API 1.x (cheap: called for every device).</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown
            && CachedApiCheck.Supports(device.Apis, AcapPlugin.ApplicationApiId, MinApplicationApi);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device) ?? TaskSupportReasons.NeedsApi("the application API", device);
    }

    /// <summary>
    /// The input for AI agents (SDK 0.13.0): install, remove, start, stop, settings and a license key. Password settings and the
    /// license key are entered by the person (<see cref="SecretsOf"/>).
    /// </summary>
    public Type? PayloadType => typeof(AcapMcpPayload);

    /// <summary>
    /// The secrets of an agent's payload (docs/spec/mcp.md "Secrets"): one per password setting (<c>password_&lt;i&gt;</c> at
    /// <c>passwords[i].value</c>, "password for MQTT.Password") and the license key of InstallLicense (<c>license_key</c>).
    /// </summary>
    public IReadOnlyList<TaskSecret> SecretsOf(string? payloadJson)
    {
        if (TryRead(payloadJson) is not { } payload)
        {
            return [];
        }

        var secrets = new List<TaskSecret>();
        var passwords = payload.Passwords ?? [];
        for (var i = 0; i < passwords.Count && i < AcapPayload.MaxPasswords; i++)
        {
            if (passwords[i] is { Name: { Length: > 0 } name })
            {
                var label = "password for " + name;
                secrets.Add(new TaskSecret(string.Create(CultureInfo.InvariantCulture, $"password_{i}"), label.Length > 64 ? label[..64] : label,
                    string.Create(CultureInfo.InvariantCulture, $"passwords[{i}].value")));
            }
        }

        if (payload.Action == AcapAction.InstallLicense && payload.LicenseKeys is not { Count: > 0 })
        {
            secrets.Add(new TaskSecret(LicenseKeySlot, "license key", LicenseKeyField));
        }

        return secrets;
    }

    /// <summary>The slot and field of an agent's license key.</summary>
    public const string LicenseKeySlot = "license_key";
    public const string LicenseKeyField = "licenseKey";

    /// <summary>The plan of an agent's InstallLicense on several devices: a key file fits one device.</summary>
    public const string OneDeviceForAKey = "A license key fits one device. Choose one device.";

    public const string LicenseKeyNote = "Installs the license key the user enters.";

    /// <summary>"Sets MQTT.Password with the value the user enters." (never the value).</summary>
    public static string PasswordsNote(IReadOnlyList<string> names) =>
        "Sets " + string.Join(", ", names) + (names.Count == 1 ? " with the value the user enters." : " with the values the user enters.");

    /// <summary>The names and enum texts the task reads.</summary>
    public System.Text.Json.JsonSerializerOptions? PayloadJsonOptions => AcapPlugin.Json;

    /// <summary>
    /// The dialog's payload rules before any task starts: the action, the package or the application name, and the settings
    /// that need no device (<see cref="AppSettings.OfflineProblems"/>, at <c>values</c> or <c>values.&lt;name&gt;</c>).
    /// </summary>
    public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        try
        {
            var read = TryRead(payloadJson);

            // The secrets the person enters, each at its field (the preview checks an entered value there).
            var secretErrors = new List<PayloadError>();
            for (var i = 0; read?.Passwords is { } passwords && i < passwords.Count; i++)
            {
                var field = string.Create(CultureInfo.InvariantCulture, $"passwords[{i}].value");
                if (passwords[i] is { } password && string.IsNullOrEmpty(password.Value))
                {
                    secretErrors.Add(new(field, AcapPayload.EnterPassword));
                }
                else if (passwords[i] is { Name: { } name } entered
                    && AppSettings.OfflineProblems(new Dictionary<string, string> { [name] = entered.Value! }, passwordsAllowed: true) is [var problem, ..])
                {
                    return [new(field, problem.Message)]; // the text rules of every setting (a person's value too)
                }
            }

            if (read is { Action: AcapAction.InstallLicense, LicenseKeys: not { Count: > 0 } })
            {
                if (string.IsNullOrEmpty(read.AgentLicenseKey))
                {
                    secretErrors.Add(new(LicenseKeyField, AcapPayload.EnterLicenseKey));
                }
                else
                {
                    try
                    {
                        LicenseKey.Parse(read.AgentLicenseKey);
                    }
                    catch (FormatException)
                    {
                        return [new(LicenseKeyField, AcapPayload.BadLicenseKey)];
                    }
                }
            }

            // The settings first, each at its own field (the parser below names only the first problem of the list).
            if (read is { Action: AcapAction.Settings } settings && (settings.Values is { Count: > 0 } || settings.Passwords is not { Count: > 0 })
                && AppSettings.OfflineProblems(settings.Values, settings.PasswordsAllowed) is { Count: > 0 } problems)
            {
                return [.. problems.Select(p => new PayloadError(p.Name is null ? "values" : "values." + p.Name, p.Message))];
            }

            AcapPayload.Parse(payloadJson, requireSecrets: false);
            return secretErrors;
        }
        catch (AcapPayloadException ex)
        {
            return [new(ex.Field, ex.Message)];
        }
        catch (ArgumentException ex)
        {
            var cut = ex.Message.IndexOf(" (Parameter '", StringComparison.Ordinal);
            var message = cut < 0 ? ex.Message : ex.Message[..cut];
            return [new(message.StartsWith("The application name", StringComparison.Ordinal) ? "application" : string.Empty, message)];
        }
    }

    /// <summary>The payload as written, before the parser's rules; null when it is no payload at all.</summary>
    private static AcapPayload? TryRead(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AcapPayload>(payloadJson, AcapPlugin.Json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The warning of a settings change: the installed applications and their settings are not in the cached data, so each
    /// device checks the values before its write.
    /// </summary>
    public const string SettingsCheckedOnDeviceWarning =
        "Each device checks the values first. A device without the application, or where a value does not fit, is not changed.";

    /// <summary>
    /// The devices the task can run on; what a package or an application needs is checked per device when its task runs (the
    /// package and the installed applications are not in the cached data). Removing says that the settings are lost, a settings
    /// change that each device checks the values first (<see cref="SettingsCheckedOnDeviceWarning"/>).
    /// </summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct) =>
        PlanCoreAsync(payloadJson, devices, null, ct);

    /// <summary>The fixed warning of an install: the package is code that runs on the devices.</summary>
    public const string PackageWarning = "The package runs on the devices. Check that it is the one you want.";

    /// <summary>The note for an upload the plan cannot find: nothing is run from it until the task reads it again.</summary>
    public const string PackageMissingWarning = "A package of this install is not among your uploads. Upload it again.";

    /// <summary>
    /// The same plan naming each uploaded package an install would use, with its name and SHA-256 from the upload store, and the
    /// fixed warning that the package runs on the devices.
    /// </summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(files);
        return PlanCoreAsync(payloadJson, devices, files, ct);
    }

    private async Task<TaskPlan> PlanCoreAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles? files, CancellationToken ct)
    {
        var payload = AcapPayload.Parse(payloadJson, requireSecrets: false); // an agent's preview: the person enters the secrets later
        var plan = TaskPlan.ByCanRun(this, devices);
        if (payload.Action == AcapAction.InstallLicense && payload.LicenseKeys.Count == 0)
        {
            // An agent's key (entered later) is one file for one device.
            return devices.Count > 1
                ? new TaskPlan([.. devices.Select(d => new TaskPlanDevice(d.Id, OneDeviceForAKey))]) { Warnings = [LicenseKeyNote] }
                : plan with { Warnings = [LicenseKeyNote] };
        }
        if (payload.Action == AcapAction.Remove)
        {
            return plan with { Warnings = ["The application's settings are lost with it."] };
        }

        if (payload.Action == AcapAction.Settings)
        {
            return plan with
            {
                Warnings = payload.Passwords is { Count: > 0 } passwords
                    ? [PasswordsNote([.. passwords.Select(p => p.Name)]), SettingsCheckedOnDeviceWarning]
                    : [SettingsCheckedOnDeviceWarning],
            };
        }

        if (payload.Action != AcapAction.Install)
        {
            return plan;
        }

        if (files is null)
        {
            return plan; // the dialog's own preview: no upload lookup
        }

        var named = new List<TaskPlanFile>();
        var missing = false;
        var zip = !string.IsNullOrWhiteSpace(payload.ZipFileId);
        foreach (var id in zip ? [payload.ZipFileId!] : payload.AllFiles().Select(f => f.FileId))
        {
            if (await files.FindAsync(id, ct).ConfigureAwait(false) is { } file)
            {
                named.Add(new TaskPlanFile(file.Name, file.Sha256));
            }
            else
            {
                missing = true;
            }
        }

        if (missing)
        {
            return plan with { Files = named, Warnings = [PackageWarning, PackageMissingWarning] };
        }

        if (!zip && payload.AllFiles().Count == 1 && payload.Rules is not { Count: > 0 } && payload.Unmatched is not AcapUnmatched.Skip)
        {
            return plan with { Files = named, Warnings = [PackageWarning] }; // one package without rows: each device checks it when its task runs
        }

        // Several builds or a ZIP: the server reads them and plans the build of each device group from the cached data.
        PackageSet set;
        try
        {
            set = await ReadPackagesAsync(files, payload, null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return plan with { Errors = [new PayloadError(zip ? "zipFileId" : "files", ex.Message)] };
        }

        if (zip)
        {
            named.AddRange(set.ZipBuilds.Select(b => new TaskPlanFile($"{set.Zip!.Name}: {b.Name}", b.Sha256)));
        }

        return PlanBuilds(plan, devices, set, payload.Mapping) with { Files = named, Warnings = [PackageWarning] };
    }

    /// <summary>At most this many lines of the plan say which build a group gets.</summary>
    public const int MaxPlanDetails = 50;

    /// <summary>
    /// The build of every device group from the cached model, AXIS OS and architecture (<see cref="AcapBuildChooser"/>): a line
    /// per group, "No package" skips its devices, a group OADM cannot decide is an error at <c>mapping</c> with its candidates.
    /// A device whose architecture or AXIS OS is not known yet runs: its task reads the device and chooses.
    /// </summary>
    internal static TaskPlan PlanBuilds(TaskPlan plan, IReadOnlyList<IDeviceInfo> devices, PackageSet set, IReadOnlyList<AcapPackageMapping>? mapping)
    {
        var byId = new Dictionary<Guid, IDeviceInfo>(devices.Count);
        foreach (var device in devices)
        {
            byId.TryAdd(device.Id, device);
        }

        var groups = new Dictionary<string, (AcapDeviceGroup Group, AcapBuildChoice? Choice, int Count)>(StringComparer.Ordinal);
        var planned = new List<TaskPlanDevice>(plan.Devices.Count);
        foreach (var entry in plan.Devices)
        {
            if (entry.SkipReason is not null || !byId.TryGetValue(entry.DeviceId, out var device) || AcapDeviceGroup.Of(device) is not { IsKnown: true } group)
            {
                planned.Add(entry);
                continue;
            }

            if (!groups.TryGetValue(group.Key, out var known))
            {
                known = (group, AcapBuildChooser.Choose(set.Builds, group, set.Rules, mapping, set.Unmatched), 0);
            }

            groups[group.Key] = known with { Count = known.Count + 1 };
            var choice = known.Choice!;
            planned.Add(choice.IsChosen || choice.IsUnclear ? entry : new TaskPlanDevice(entry.DeviceId, choice.Reason ?? AcapCompatibility.NoPackageFor(group.Architecture)));
        }

        var errors = new List<PayloadError>();
        var details = new List<string>();
        foreach (var (group, choice, count) in groups.Values.OrderByDescending(g => g.Count).ThenBy(g => g.Group.Label, StringComparer.Ordinal))
        {
            var devicesText = string.Create(CultureInfo.InvariantCulture, $"{group.Label} ({count:N0} {(count == 1 ? "device" : "devices")})");
            if (choice!.IsUnclear)
            {
                var candidates = string.Join(" or ", choice.Candidates.Select(i => set.Builds[i]).Select(b => b.Key == b.Name ? b.Name : $"{b.Name} ({b.Key})"));
                errors.Add(new PayloadError("mapping", $"Choose the package for {group.Label}: {candidates}. {choice.Reason}"));
            }
            else if (details.Count < MaxPlanDetails)
            {
                details.Add(choice.IsChosen
                    ? $"{devicesText}: {set.Builds[choice.Index].Name}{(choice.DecidedBy == AcapBuildChoice.Automatic ? string.Empty : " (" + choice.DecidedBy.ToLowerInvariant() + ")")}"
                    : $"{devicesText}: {choice.Reason}");
            }
        }

        if (groups.Count > MaxPlanDetails + errors.Count)
        {
            details.Add(string.Create(CultureInfo.InvariantCulture, $"+{groups.Count - errors.Count - MaxPlanDetails:N0} more groups"));
        }

        return new TaskPlan(planned) { Errors = errors, Details = details };
    }

    /// <summary>The user's query (SDK 0.4.0): reads only the queried device, which the host checked against the user's view.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, AcapPlugin.ListApplicationsMethod, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query method \"{method}\".");
        }

        // Read-only: API list, basicdeviceinfo, param.cgi list, info.cgi, config.cgi get, list.cgi.
        (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
        var api = new ApplicationApiClient(ctx.Vapix);
        var facts = await api.GetDeviceFactsAsync(ct).ConfigureAwait(false);
        var apps = await api.ListAsync(ct).ConfigureAwait(false);
        return new ListApplicationsResult { Device = facts, Applications = apps }.ToJson();
    }

    /// <summary>The detail of Read SDK version: "2.18; ACAP 3, ACAP 4, computer vision", "Not reported" when the device says neither.</summary>
    public static string SdkText(string? version, IReadOnlyList<string>? sdks)
    {
        var kinds = sdks is null ? null : string.Join(", ", sdks.Select(s => s switch
        {
            "acap3" => "ACAP 3",
            "acap4-native" => "ACAP 4",
            "acap4-cv" => "computer vision",
            _ => s,
        }));
        return (string.IsNullOrWhiteSpace(version) ? null : version.Trim(), kinds) switch
        {
            ({ } v, { } k) => $"{v}; {k}",
            ({ } v, null) => v,
            (null, { } k) => k,
            _ => "Not reported",
        };
    }

    /// <summary>Step names (see README.md).</summary>
    public static class Steps
    {
        public const string CheckCompatibility = "Check compatibility";
        public const string ReadPackage = "Read package";
        public const string ReadDeviceInfo = "Read device info";
        public const string ReadDevelopmentVersion = "Read SDK version";
        public const string ReadUnsignedSetting = "Read unsigned application setting";
        public const string ReadApplications = "Read installed applications";
        public const string CheckPackage = "Check package";
        public const string Upload = "Upload package";
        public const string VerifyInstallation = "Verify installation";
        public const string Start = "Start application";
        public const string Stop = "Stop application";
        public const string Remove = "Remove application";
        public const string VerifyState = "Verify application state";
        public const string VerifyRemoval = "Verify removal";
        public const string CheckLicense = "Check license key";
        public const string InstallLicense = "Install license key";
        public const string VerifyLicense = "Verify license";
        public const string ReadSettings = "Read settings";
        public const string ChangeSettings = "Change settings";
        public const string VerifySettings = "Verify settings";

        /// <summary>A device of the selection without a key in the chosen files.</summary>
        public const string NoKey = LicenseKeyMatcher.NoKeyText;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = AcapPayload.ParseShared(payloadJson);
        string[] license = payload.LicenseKeys.Count > 0 ? [Steps.CheckLicense, Steps.InstallLicense, Steps.VerifyLicense] : [];
        string[] install = [Steps.CheckCompatibility, Steps.ReadPackage, Steps.ReadDevelopmentVersion, Steps.ReadDeviceInfo, Steps.ReadUnsignedSetting, Steps.ReadApplications, Steps.CheckPackage, Steps.Upload, Steps.VerifyInstallation, .. license];
        string[] planned = payload.Action switch
        {
            AcapAction.Install when payload.StartAfterInstall => [.. install, Steps.Start, Steps.VerifyState],
            AcapAction.Install => install,
            AcapAction.InstallLicense => [Steps.CheckCompatibility, Steps.ReadApplications, .. license],
            AcapAction.Remove => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Remove, Steps.VerifyRemoval],
            AcapAction.Start => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Start, Steps.VerifyState],
            AcapAction.Stop => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.Stop, Steps.VerifyState],
            AcapAction.Settings => [Steps.CheckCompatibility, Steps.ReadApplications, Steps.ReadSettings, Steps.ChangeSettings, Steps.VerifySettings],
            _ => [Steps.CheckCompatibility],
        };
        ctx.PlanSteps(planned);

        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            var api = apis.Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
            ctx.Log(TaskLogLevel.Info, $"Device offers {api.Id} {api.Version}.");
            step.Complete($"{api.Id} {api.Version}");
        }

        var client = new ApplicationApiClient(ctx.Vapix);
        switch (payload.Action)
        {
            case AcapAction.Install:
                await InstallAsync(ctx, device, client, payload, planned, ct).ConfigureAwait(false);
                break;
            case AcapAction.InstallLicense:
                await InstallLicenseOnlyAsync(ctx, device, client, payload, ct).ConfigureAwait(false);
                break;
            case AcapAction.Remove:
                await RemoveAsync(ctx, client, payload.Application!, _verifyInterval, _verifyTimeout, _time, ct).ConfigureAwait(false);
                break;
            case AcapAction.Start:
            case AcapAction.Stop:
                await StartStopAsync(ctx, client, payload.Application!, payload.Action == AcapAction.Start, ct).ConfigureAwait(false);
                break;
            case AcapAction.Settings:
                await ChangeSettingsAsync(ctx, client, payload, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException("Unknown action. Nothing was changed.", nameof(payloadJson));
        }
    }

    private async Task InstallAsync(ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, AcapPayload payload, string[] planned, CancellationToken ct)
    {
        // Every package of the payload (or of its ZIP) is read and checked first: one application, each file once.
        PackageSet set;
        using (var step = ctx.BeginStep(Steps.ReadPackage))
        {
            set = await ReadPackagesAsync(ctx.Files, payload, ctx, ct).ConfigureAwait(false);
            var first = set.Builds[0].Package;
            step.Complete(set.Builds.Count > 1
                ? $"{first.AppName} {AcapCompatibility.VersionsText(set.Builds.Select(b => b.Package))}, {string.Join(" and ", set.Builds.Select(b => b.Package.Architecture ?? "any architecture"))}"
                : $"{first.AppName} {first.Version}, {first.Architecture ?? "any architecture"}");
        }

        // One step per device request (read-only). The build is chosen again from the fresh read of the device, its SDK included.
        string? embdev;
        IReadOnlyList<string>? sdks;
        using (var step = ctx.BeginStep(Steps.ReadDevelopmentVersion))
        {
            embdev = await client.TryGetEmbeddedDevelopmentVersionAsync(ct).ConfigureAwait(false);
            sdks = await client.TryGetSupportedSdksAsync(ct).ConfigureAwait(false);
            step.Complete(SdkText(embdev, sdks));
        }

        BasicDeviceInfo info;
        int chosen;
        using (var step = ctx.BeginStep(Steps.ReadDeviceInfo))
        {
            info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var group = new AcapDeviceGroup(string.IsNullOrWhiteSpace(info.ProdNbr) ? device.Model : info.ProdNbr, info.Version, info.Architecture, info.Soc, info.ProdShortName);
            var sdkFacts = new AcapDeviceFacts { Architecture = info.Architecture, FirmwareVersion = info.Version, EmbeddedDevelopmentVersion = embdev, SupportedSdks = sdks };
            var choice = AcapBuildChooser.Choose(set.Builds, group, set.Rules, payload.Mapping, set.Unmatched, sdkFacts);
            if (choice.IsUnclear || (!choice.IsChosen && choice.DecidedBy == AcapBuildChoice.ChosenByPerson))
            {
                // Nobody chose for this group, or the choice does not fit what the device is now.
                var unclear = choice.IsUnclear ? $"{choice.Reason} Choose the package for {group.Label}." : choice.Reason;
                ctx.Log(TaskLogLevel.Error, unclear!);
                throw new InvalidOperationException(unclear + " Nothing was changed.");
            }

            if (!choice.IsChosen)
            {
                // Several packages, none for this device: skipped, nothing changed.
                var reason = choice.Reason ?? AcapCompatibility.NoPackageFor(info.Architecture);
                ctx.Log(TaskLogLevel.Warning, $"{reason} The packages are {string.Join(", ", set.Builds.Select(b => b.Name))}.");
                step.Warn(reason + " Nothing was changed.");
                foreach (var name in planned.SkipWhile(n => n != Steps.ReadDeviceInfo).Skip(1))
                {
                    ctx.SkipStep(name, reason);
                }

                return;
            }

            chosen = choice.Index;
            if (set.Builds.Count > 1)
            {
                var build = set.Builds[chosen];
                ctx.Log(TaskLogLevel.Info, $"Uses {build.Name} ({build.Package.Version}, {build.Package.Architecture ?? "any architecture"}; {choice.DecidedBy.ToLowerInvariant()}).");
                step.Complete($"AXIS OS {info.Version}, {info.Architecture}: {build.Name}");
            }
            else
            {
                step.Complete($"AXIS OS {info.Version}, {info.Architecture}");
            }
        }

        var package = set.Builds[chosen].Package;
        var (fileName, fileSize) = set.SizeOf(chosen);

        bool? allowUnsigned;
        using (var step = ctx.BeginStep(Steps.ReadUnsignedSetting))
        {
            allowUnsigned = await client.TryGetAllowUnsignedAsync(ct).ConfigureAwait(false);
            step.Complete(allowUnsigned switch
            {
                true => "Unsigned applications allowed",
                false => "Only signed applications allowed",
                null => "Not reported",
            });
        }

        var facts = new AcapDeviceFacts
        {
            Architecture = info.Architecture,
            FirmwareVersion = info.Version,
            EmbeddedDevelopmentVersion = embdev,
            SupportedSdks = sdks,
            AllowUnsigned = allowUnsigned,
        };

        InstalledApplication? installed;
        IReadOnlyList<InstalledApplication> installedApps;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            var before = await client.ListAsync(ct).ConfigureAwait(false);
            installedApps = before;
            installed = before.FirstOrDefault(a => string.Equals(a.Name, package.AppName, StringComparison.Ordinal));
            step.Complete(installed is null
                ? $"{before.Count} installed, {package.AppName} not installed"
                : $"{before.Count} installed, {package.AppName} {installed.Version} ({installed.Status})");
        }

        AcapCompatibilityReport report;
        using (var step = ctx.BeginStep(Steps.CheckPackage))
        {
            report = AcapCompatibility.Check(package, facts, installed, payload.AllowDowngrade, installedApps);
            if (!report.IsCompatible)
            {
                foreach (var problem in report.Problems)
                {
                    ctx.Log(TaskLogLevel.Error, problem);
                }

                throw new InvalidOperationException(string.Join(" ", report.Problems) + " Nothing was changed.");
            }

            foreach (var warning in report.Warnings)
            {
                ctx.Log(TaskLogLevel.Warning, warning);
            }

            ctx.Log(TaskLogLevel.Info, $"{report.KindText} on AXIS OS {facts.FirmwareVersion} ({facts.Architecture}).");
            step.Complete(report.KindText);
        }

        var answerLost = false;
        using (var step = ctx.BeginStep(Steps.Upload))
        {
            var upload = await set.OpenAsync(ctx.Files, chosen, ct).ConfigureAwait(false);
            await using (upload.ConfigureAwait(false))
            {
                var lastPercent = -1;
                var progress = new SyncProgress<long>(bytes =>
                {
                    var percent = (int)(100 * Math.Min(1.0, fileSize <= 0 ? 0 : (double)bytes / fileSize));
                    if (percent > lastPercent)
                    {
                        lastPercent = percent;
                        step.ReportProgress(percent, percent >= 100
                            ? "The device installs the package"
                            : ByteSize.Progress(bytes, fileSize));
                    }
                });
                try
                {
                    if (_beforeWrite is not null) await _beforeWrite(ctx.Vapix, AcapAction.Install, ct).ConfigureAwait(false);
                    await client.UploadAsync(upload, fileName, progress, ct).ConfigureAwait(false);
                    step.Complete(ByteSize.Text(fileSize) + ", installed by the device");
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TaskCanceledException or TimeoutException or HttpRequestException || ex is AcapDeviceException { OutcomeUnknown: true }))
                {
                    // The device answers upload.cgi only after installing; slow installs can outlive the HTTP timeout, and
                    // error 15 says the device itself does not know the outcome.
                    answerLost = true;
                    ctx.Log(TaskLogLevel.Warning, "No answer to the upload in time (" + ex.Message + "); checking whether the installation completed.");
                    step.Complete("No answer in time; the installation is verified next");
                }
                catch (AcapDeviceException ex) when (ex.Code == 2 && allowUnsigned != false)
                {
                    throw new AcapDeviceException(2, "The package signature is not valid. Download the package again. (error 2)");
                }
                catch (AcapDeviceException ex) when (installed is not null)
                {
                    // VAPIX: a failed or interrupted upgrade removes both versions. Say so when it happened.
                    if (await TryFindAsync(client, package.AppName, ct).ConfigureAwait(false) is false)
                    {
                        ctx.Log(TaskLogLevel.Warning, $"{installed.DisplayName} is no longer installed after the failed upgrade.");
                        throw new AcapDeviceException(ex.Code, $"{ex.Message} The device removed {installed.DisplayName} too: install it again; its settings come back.");
                    }

                    throw;
                }
            }
        }

        InstalledApplication app;
        using (var step = ctx.BeginStep(Steps.VerifyInstallation))
        {
            app = await WaitForVersionAsync(client, package.AppName, package.Version, answerLost ? _verifyTimeout : TimeSpan.Zero, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(answerLost
                    ? $"The device did not confirm the installation and {package.AppName} {package.Version} is not in its application list."
                    : $"The device reported success but {package.AppName} {package.Version} is not in its application list.");

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} {app.Version} is installed ({app.Status}).");
            if (answerLost)
            {
                step.Warn($"The upload was not confirmed in time, but {package.AppName} {package.Version} is installed.");
            }
            else
            {
                var done = report.Kind switch
                {
                    InstallKind.Upgrade => "Upgraded",
                    InstallKind.Reinstall => "Reinstalled",
                    InstallKind.Downgrade => "Downgraded",
                    _ => "Installed",
                };
                step.Complete($"{done} {app.DisplayName} {app.Version} ({app.Status})");
            }
        }

        if (payload.LicenseKeys.Count > 0)
        {
            // The application is installed: a key problem of this device is a warning, never a failure.
            if (await InstallLicenseAsync(ctx, device, client, app, payload, failOnProblem: false, ct).ConfigureAwait(false) is { } licensed)
            {
                app = licensed;
            }
        }

        if (!payload.StartAfterInstall)
        {
            return;
        }

        if (app.IsRunning)
        {
            ctx.SkipStep(Steps.Start, $"{app.DisplayName} is already running.");
            ctx.SkipStep(Steps.VerifyState, $"{app.DisplayName} is already running.");
            return;
        }

        using (var step = ctx.BeginStep(Steps.Start))
        {
            if (_beforeWrite is not null) await _beforeWrite(ctx.Vapix, AcapAction.Start, ct).ConfigureAwait(false);
            await ControlTolerantAsync(client, AcapControlAction.Start, package.AppName, ct).ConfigureAwait(false);
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyState))
        {
            var after = await WaitForStateAsync(client, package.AppName, running: true, ct).ConfigureAwait(false);
            if (after?.IsRunning == true)
            {
                ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is running.");
                step.Complete($"{app.DisplayName} is running");
            }
            else
            {
                step.Warn($"{app.DisplayName} was installed but did not start: {NotStartedReason(after)}");
            }
        }
    }

    /// <summary>
    /// The builds of an install: the uploaded .eap files, or the .eap entries of one uploaded ZIP (read as streams, never
    /// unpacked to disk), the mapping rows (the payload's, or the ZIP's <c>oadm-packages.json</c>) and what a device no row
    /// matches gets, checked as one set.
    /// </summary>
    internal sealed record PackageSet(IReadOnlyList<AcapBuild> Builds, IReadOnlyList<AcapPackageRule> Rules, IReadOnlyList<UploadedFile> Files, UploadedFile? Zip, IReadOnlyList<EapZip.ZipBuild> ZipBuilds,
        AcapUnmatched Unmatched = AcapUnmatched.Automatic)
    {
        /// <summary>The name and size of the file the device gets.</summary>
        public (string Name, long Size) SizeOf(int index) => Zip is null ? (Files[index].Name, Files[index].Size) : (ZipBuilds[index].Name, ZipBuilds[index].Size);

        /// <summary>The content of a build: the uploaded file, or its entry of the ZIP within its declared size.</summary>
        public async Task<Stream> OpenAsync(IUploadedFiles files, int index, CancellationToken ct)
        {
            if (Zip is null)
            {
                return await files.OpenReadAsync(Files[index].Id, ct).ConfigureAwait(false);
            }

            var stream = await files.OpenReadAsync(Zip.Id, ct).ConfigureAwait(false);
            try
            {
                return await EapZip.OpenBuildAsync(stream, Zip.Name, ZipBuilds[index].EntryName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The ZIP could not be opened: its file is closed here (an archive that opened closed it already).
                await stream.DisposeAsync().ConfigureAwait(false);
                if (ex is InvalidEapException invalid)
                {
                    throw new InvalidOperationException(invalid.Message + " Nothing was changed.", invalid);
                }

                throw;
            }
        }
    }

    /// <summary>The text when the uploaded ZIP is gone.</summary>
    public const string ZipGone = "The uploaded ZIP is no longer available on the server. Nothing was changed.";

    /// <summary>Reads every build of the payload and checks them as one set; any problem fails "Nothing was changed".</summary>
    internal static async Task<PackageSet> ReadPackagesAsync(IUploadedFiles files, AcapPayload payload, ITaskExecutionContext? log, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(payload.ZipFileId))
        {
            var zip = await files.FindAsync(payload.ZipFileId, ct).ConfigureAwait(false) ?? throw new InvalidOperationException(ZipGone);
            EapZip.ZipContents contents;
            var stream = await files.OpenReadAsync(zip.Id, ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                try
                {
                    contents = await EapZip.ReadAsync(stream, zip.Name, ct).ConfigureAwait(false);
                }
                catch (InvalidEapException ex)
                {
                    throw new InvalidOperationException(ex.Message + " Nothing was changed.", ex);
                }
            }

            var builds = contents.Builds.Select(b => new AcapBuild(b.Name, b.Name, b.Package)).ToList();
            CheckSet(builds, payload.Application);
            foreach (var b in contents.Builds)
            {
                log?.Log(TaskLogLevel.Info, $"Package {b.Package.AppName} {b.Package.Version} ({b.Package.Architecture ?? "any architecture"}) from {zip.Name}: {b.Name}, {b.Size} bytes, SHA-256 {b.Sha256}.");
            }

            return new PackageSet(builds, contents.Rules, [], zip, contents.Builds, contents.Unmatched);
        }

        var entries = payload.AllFiles();
        var read = new List<(UploadedFile File, EapManifest Package)>(entries.Count);
        foreach (var entry in entries)
        {
            read.Add(await ReadPackageAsync(files, log, entry, payload.Application, ct).ConfigureAwait(false));
        }

        var set = read.Select((r, i) => new AcapBuild(entries[i].FileId, r.File.Name, r.Package)).ToList();
        CheckSet(set, payload.Application);
        return new PackageSet(set, payload.Rules ?? [], [.. read.Select(r => r.File)], null, [], payload.Unmatched ?? AcapUnmatched.Automatic);
    }

    private static void CheckSet(List<AcapBuild> builds, string? expectedName)
    {
        if (AcapCompatibility.CheckPackageSet([.. builds.Select(b => (b.Name, b.Package))]) is { } refused)
        {
            throw new InvalidOperationException(refused + " Nothing was changed.");
        }

        if (expectedName is { Length: > 0 } && !string.Equals(expectedName, builds[0].Package.AppName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The package is \"{builds[0].Package.AppName}\", the dialog checked \"{expectedName}\". Nothing was changed.");
        }
    }

    /// <summary>One uploaded package: still there, the SHA-256 the dialog checked, a readable .eap of the expected application.</summary>
    private static async Task<(UploadedFile File, EapManifest Package)> ReadPackageAsync(IUploadedFiles files, ITaskExecutionContext? log, AcapPackageFile entry, string? expectedName, CancellationToken ct)
    {
        var file = await files.FindAsync(entry.FileId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The uploaded package is no longer available on the server. Nothing was changed.");
        if (entry.Sha256 is { Length: > 0 } sha && !string.Equals(sha, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The uploaded package does not match the one checked in the dialog. Nothing was changed.");
        }

        EapManifest package;
        var read = await files.OpenReadAsync(file.Id, ct).ConfigureAwait(false);
        await using (read.ConfigureAwait(false))
        {
            try
            {
                package = await EapReader.ReadAsync(read, ct).ConfigureAwait(false);
            }
            catch (InvalidEapException ex)
            {
                throw new InvalidOperationException(ex.Message + " Nothing was changed.", ex);
            }
        }

        if (expectedName is { Length: > 0 } && !string.Equals(expectedName, package.AppName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The package is \"{package.AppName}\", the dialog checked \"{expectedName}\". Nothing was changed.");
        }

        if (entry.Architecture is { Length: > 0 } declared && !AcapCompatibility.IsArchitectureIndependent(declared)
            && (AcapCompatibility.IsArchitectureIndependent(package.Architecture)
                || AcapCompatibility.NormalizeArchitecture(declared) != AcapCompatibility.NormalizeArchitecture(package.Architecture!)))
        {
            throw new InvalidOperationException($"{file.Name} is not the {declared} package the dialog checked. Nothing was changed.");
        }

        log?.Log(TaskLogLevel.Info, $"Package {package.AppName} {package.Version} ({package.Architecture ?? "any architecture"}) from {file.Name}, {file.Size} bytes, read from {package.Source}.");
        return (file, package);
    }

    /// <summary>
    /// Change settings: the application is installed, every name is one of its settings and every value fits the type and
    /// range, else Failed with nothing changed; only values that differ are sent, in one request; then read back (passwords are
    /// never read back). Values never reach a log or a step text, only names.
    /// </summary>
    private static async Task ChangeSettingsAsync(ITaskExecutionContext ctx, ApplicationApiClient client, AcapPayload payload, CancellationToken ct)
    {
        var name = payload.Application!;
        InstalledApplication app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{payload.AppDisplayName ?? name} is not installed on this device. Nothing was changed.");
            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        var values = payload.AllValues();
        Dictionary<string, string> changed;
        IReadOnlyList<AppSettingDefinition> definitions;
        HashSet<string> passwordNames;
        using (var step = ctx.BeginStep(Steps.ReadSettings))
        {
            definitions = await client.ReadSettingsAsync(name, ct).ConfigureAwait(false);
            if (definitions.Count == 0)
            {
                throw new InvalidOperationException($"{app.DisplayName} has no settings here. Nothing was changed.");
            }

            var problems = AppSettings.Check(definitions, values, app.DisplayName, out var normalized, payload.PasswordsAllowed, payload.EnteredPasswords());
            if (problems.Count > 0)
            {
                foreach (var problem in problems)
                {
                    ctx.Log(TaskLogLevel.Error, problem);
                }

                throw new InvalidOperationException(string.Join(" ", problems) + " Nothing was changed.");
            }

            var current = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                current.TryAdd(definition.Name, definition);
            }

            passwordNames = [.. current.Values.Where(d => d.Kind == AppSettingKind.Password).Select(d => d.Name)];
            changed = normalized
                .Where(v => current[v.Key].Kind == AppSettingKind.Password || !string.Equals(current[v.Key].Value, v.Value, StringComparison.Ordinal))
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            step.Complete(changed.Count == values.Count
                ? Count(definitions.Count, "setting")
                : string.Create(CultureInfo.InvariantCulture, $"{Count(definitions.Count, "setting")}, {values.Count - changed.Count:N0} already set"));
        }

        if (changed.Count == 0)
        {
            ctx.Log(TaskLogLevel.Info, $"Every setting of {app.DisplayName} already has its new value.");
            ctx.SkipStep(Steps.ChangeSettings, "Every value is already set.");
            ctx.SkipStep(Steps.VerifySettings, "Every value is already set.");
            return;
        }

        var names = string.Join(", ", changed.Keys.Order(StringComparer.Ordinal));
        using (var step = ctx.BeginStep(Steps.ChangeSettings))
        {
            ctx.Log(TaskLogLevel.Info, $"Changing settings of {app.DisplayName}: {names}.");
            await client.UpdateSettingsAsync(name, changed, ct).ConfigureAwait(false);
            step.Complete(names);
        }

        using (var step = ctx.BeginStep(Steps.VerifySettings))
        {
            var after = new Dictionary<string, AppSettingDefinition>(StringComparer.Ordinal);
            foreach (var definition in await client.ReadSettingsAsync(name, ct).ConfigureAwait(false))
            {
                after.TryAdd(definition.Name, definition);
            }

            var passwords = changed.Keys.Count(passwordNames.Contains);
            var differ = changed
                .Where(v => !(after.TryGetValue(v.Key, out var d) && (d.Kind == AppSettingKind.Password || SameValue(d, v.Value))))
                .Select(v => v.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (differ.Count > 0)
            {
                throw new InvalidOperationException($"The device did not keep the new value of {string.Join(", ", differ)}.");
            }

            ctx.Log(TaskLogLevel.Info, $"Settings of {app.DisplayName} changed: {names}.");
            step.Complete(passwords == 0
                ? Count(changed.Count, "setting") + " changed"
                : string.Create(CultureInfo.InvariantCulture, $"{Count(changed.Count, "setting")} changed, {Count(passwords, "password")} not read back"));
        }
    }

    /// <summary>The device kept the value (numbers by value, yes/no and list values as the device spells them).</summary>
    private static bool SameValue(AppSettingDefinition definition, string sent) =>
        string.Equals(definition.Value, sent, StringComparison.Ordinal)
        || (AppSettings.TryNormalize(definition, definition.Value, out var kept, out _) && string.Equals(kept, sent, StringComparison.Ordinal));

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n:N0} {noun}{(n == 1 ? string.Empty : "s")}");

    private async Task InstallLicenseOnlyAsync(ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, AcapPayload payload, CancellationToken ct)
    {
        var name = payload.Application!;
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"{payload.AppDisplayName ?? name} is not installed on this device. Nothing was changed.");
            step.Complete($"{app.DisplayName} {app.Version}, license {LicenseText(app)}");
        }

        await InstallLicenseAsync(ctx, device, client, app, payload, failOnProblem: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Check license key, Install license key, Verify license. The key must name this device's MAC address and the installed
    /// application and must not be expired; it is checked before anything is sent. Without a key for this device: a warning,
    /// nothing sent. Another problem fails the task (license only) or is a warning (after an install). Returns the
    /// application as listed after the key, null when no key was installed.
    /// </summary>
    private async Task<InstalledApplication?> InstallLicenseAsync(
        ITaskExecutionContext ctx, IDeviceInfo device, ApplicationApiClient client, InstalledApplication app, AcapPayload payload, bool failOnProblem, CancellationToken ct)
    {
        LicenseKey? key = null;
        using (var step = ctx.BeginStep(Steps.CheckLicense))
        {
            var mac = LicenseKey.NormalizeMac(device.Serial);
            var entry = mac is null ? null : FittingKey(payload.KeysFor(mac), app.Version);
            string? problem = null;
            if (entry is null)
            {
                step.Warn(Steps.NoKey + " Nothing was sent.");
            }
            else
            {
                try
                {
                    key = LicenseKey.Parse(entry.Xml);
                    problem = KeyProblem(key, mac!, app, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime), payload.LicenseApplicationId);
                }
                catch (FormatException ex)
                {
                    problem = ex.Message;
                }

                if (problem is not null)
                {
                    key = null;
                    if (failOnProblem)
                    {
                        throw new InvalidOperationException(problem + " Nothing was changed.");
                    }

                    step.Warn(problem + " The key was not installed.");
                }
                else
                {
                    step.Complete($"Key for {app.DisplayName}, {key!.ValidityText}");
                }
            }
        }

        if (key is null)
        {
            ctx.SkipStep(Steps.InstallLicense, "No usable key.");
            ctx.SkipStep(Steps.VerifyLicense, "No usable key.");
            return null;
        }

        using (var step = ctx.BeginStep(Steps.InstallLicense))
        {
            try
            {
                await client.UploadLicenseKeyAsync(app.Name, key.Xml, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!failOnProblem && !ct.IsCancellationRequested && ex is AcapDeviceException or UnauthorizedAccessException or TaskCanceledException or TimeoutException or HttpRequestException)
            {
                step.Warn(ex.Message);
                ctx.SkipStep(Steps.VerifyLicense, "The key was not installed.");
                return null;
            }

            ctx.Log(TaskLogLevel.Info, $"License key for {app.DisplayName} installed ({key.ValidityText}).");
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyLicense))
        {
            var after = await FindAsync(client, app.Name, ct).ConfigureAwait(false);
            if (after is not null && string.Equals(after.License, "Valid", StringComparison.OrdinalIgnoreCase))
            {
                step.Complete($"{after.DisplayName}: license {LicenseText(after)}");
            }
            else
            {
                step.Warn($"The device took the key, but shows the license of {app.DisplayName} as {after?.License ?? "unknown"}.");
            }

            return after;
        }
    }

    /// <summary>The key that fits the installed version, else the first (its problem then says why).</summary>
    private static LicenseKeyEntry? FittingKey(IReadOnlyList<LicenseKeyEntry> entries, string? installedVersion)
    {
        foreach (var entry in entries)
        {
            try
            {
                if (LicenseKey.Parse(entry.Xml).Fits(installedVersion))
                {
                    return entry;
                }
            }
            catch (FormatException)
            {
            }
        }

        return entries.Count > 0 ? entries[0] : null;
    }

    /// <summary>
    /// Why <paramref name="key"/> cannot be installed for <paramref name="app"/> on the device <paramref name="mac"/>; null when it
    /// can. The application id is the device's, else <paramref name="expectedApplicationId"/> (the package's or the chosen one).
    /// </summary>
    public static string? KeyProblem(LicenseKey key, string mac, InstalledApplication app, DateOnly today, string? expectedApplicationId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(app);
        if (key.DeviceId != mac)
        {
            return $"The key is for the device {key.DeviceId}, not {mac}.";
        }

        var id = app.ApplicationId is { Length: > 0 } reported && reported != "0" ? reported
            : expectedApplicationId is { Length: > 0 } expected && expected != "0" ? expected
            : null;
        if (id is not null && key.ApplicationId != id)
        {
            return $"The key is for another application (ID {key.ApplicationId}; {app.DisplayName} has ID {id}).";
        }

        if (key.IsExpired(today))
        {
            return $"The key expired on {key.ExpirationDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
        }

        return key.Fits(app.Version) ? null : $"The key is for {key.VersionRangeText} of {app.DisplayName}; {app.Version} is installed.";
    }

    /// <summary>"Valid until 2027-09-18", "Not required", ... (<see cref="InstalledApplication.LicenseText"/>).</summary>
    public static string LicenseText(InstalledApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.LicenseText;
    }

    private static async Task RemoveAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, TimeSpan interval, TimeSpan timeout, TimeProvider time, CancellationToken ct)
    {
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null)
            {
                step.Warn($"{name} is not installed on this device; nothing to remove.");
                ctx.SkipStep(Steps.Remove, "Not installed.");
                ctx.SkipStep(Steps.VerifyRemoval, "Not installed.");
                return;
            }

            if (app.Bundled)
            {
                throw new InvalidOperationException($"{app.DisplayName} is bundled with AXIS OS and cannot be removed. Nothing was changed.");
            }

            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        var answerLost = false;
        using (var step = ctx.BeginStep(Steps.Remove))
        {
            ctx.Log(TaskLogLevel.Info, $"Removing {app.DisplayName} {app.Version} ({app.Status}).");
            try
            {
                await client.ControlAsync(AcapControlAction.Remove, name, ct).ConfigureAwait(false);
                step.Complete(app.DisplayName);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (ex is TaskCanceledException or TimeoutException or HttpRequestException || ex is AcapDeviceException { OutcomeUnknown: true }))
            {
                // The device may still be removing it (or says the outcome is unknown): the next step looks for it until it is gone.
                answerLost = true;
                ctx.Log(TaskLogLevel.Warning, "No answer to the remove command in time (" + ex.Message + "); checking whether the application is gone.");
                step.Complete("No answer in time; the removal is verified next");
            }
        }

        using (var step = ctx.BeginStep(Steps.VerifyRemoval))
        {
            var start = time.GetTimestamp();
            while (await IsInstalledAsync(client, name, answerLost, ct).ConfigureAwait(false) != false)
            {
                if (!answerLost || time.GetElapsedTime(start) >= timeout)
                {
                    throw new InvalidOperationException(answerLost
                        ? $"The device did not confirm the removal and {app.DisplayName} is still installed."
                        : $"The device accepted the remove command but {app.DisplayName} is still installed.");
                }

                await Task.Delay(interval, time, ct).ConfigureAwait(false);
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} removed.");
            if (answerLost)
            {
                step.Warn($"The remove command was not confirmed in time, but {app.DisplayName} is no longer installed.");
            }
            else
            {
                step.Complete($"{app.DisplayName} is no longer installed");
            }
        }
    }

    private async Task StartStopAsync(ITaskExecutionContext ctx, ApplicationApiClient client, string name, bool start, CancellationToken ct)
    {
        var verb = start ? "Start" : "Stop";
        var control = start ? Steps.Start : Steps.Stop;
        var wanted = start ? "running" : "stopped";
        InstalledApplication? app;
        using (var step = ctx.BeginStep(Steps.ReadApplications))
        {
            app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null)
            {
                step.Warn($"{name} is not installed on this device; skipped.");
                ctx.SkipStep(control, "Not installed.");
                ctx.SkipStep(Steps.VerifyState, "Not installed.");
                return;
            }

            step.Complete($"{app.DisplayName} {app.Version} ({app.Status})");
        }

        if (app.IsRunning == start)
        {
            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is already {wanted}.");
            ctx.SkipStep(control, $"{app.DisplayName} is already {wanted}.");
            ctx.SkipStep(Steps.VerifyState, $"{app.DisplayName} is already {wanted}.");
            return;
        }

        using (var step = ctx.BeginStep(control))
        {
            ctx.Log(TaskLogLevel.Info, $"{verb} {app.DisplayName} {app.Version}.");
            await ControlTolerantAsync(client, start ? AcapControlAction.Start : AcapControlAction.Stop, name, ct).ConfigureAwait(false);
            step.Complete(app.DisplayName);
        }

        using (var step = ctx.BeginStep(Steps.VerifyState))
        {
            var after = await WaitForStateAsync(client, name, start, ct).ConfigureAwait(false);
            if (after is null || after.IsRunning != start)
            {
                if (start)
                {
                    throw new InvalidOperationException($"{app.DisplayName} did not start: {NotStartedReason(after)}");
                }

                throw new InvalidOperationException($"{app.DisplayName} did not stop (status {after?.Status ?? "unknown"}).");
            }

            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} is {after.Status}.");
            step.Complete($"{app.DisplayName} is {after.Status}");
        }
    }

    /// <summary>
    /// Whether the application is listed; null while polling and the device does not answer (it may still be busy removing).
    /// </summary>
    private static async Task<bool?> IsInstalledAsync(ApplicationApiClient client, string name, bool polling, CancellationToken ct)
    {
        try
        {
            return await FindAsync(client, name, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (polling && !ct.IsCancellationRequested && ex is TaskCanceledException or TimeoutException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Start/stop treating "already running" (6) and "not running" (7) as success, and an unknown outcome (15, no answer) as
    /// something the next step verifies.
    /// </summary>
    private static async Task ControlTolerantAsync(ApplicationApiClient client, AcapControlAction action, string name, CancellationToken ct)
    {
        try
        {
            await client.ControlAsync(action, name, ct).ConfigureAwait(false);
        }
        catch (AcapDeviceException ex) when ((action == AcapControlAction.Start && ex.Code == 6) || (action == AcapControlAction.Stop && ex.Code == 7))
        {
            // Already in the requested state.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is AcapDeviceException { OutcomeUnknown: true } or TaskCanceledException or TimeoutException or HttpRequestException))
        {
            // The device may still be starting or stopping it: the state is verified next.
        }
    }

    /// <summary>Polls until the application is (not) running, at most 15 s (an application needs a moment to start).</summary>
    private async Task<InstalledApplication?> WaitForStateAsync(ApplicationApiClient client, string name, bool running, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        var limit = _verifyTimeout < TimeSpan.FromSeconds(15) ? _verifyTimeout : TimeSpan.FromSeconds(15);
        while (true)
        {
            var app = await FindAsync(client, name, ct).ConfigureAwait(false);
            if (app is null || app.IsRunning == running || _time.GetElapsedTime(start) >= limit)
            {
                return app;
            }

            await Task.Delay(_verifyInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Why an application is not running: its license when that is the reason, else its status.</summary>
    public static string NotStartedReason(InstalledApplication? app) => app switch
    {
        null => "it is no longer installed.",
        { License: "Missing" } => "its license is missing. Install a license key.",
        { License: "Invalid" } => "its license is not valid. Install a new license key.",
        _ => $"its status is {app.Status ?? "unknown"}. Check its settings.",
    };

    /// <summary>Whether the application is listed; null when the list cannot be read.</summary>
    private static async Task<bool?> TryFindAsync(ApplicationApiClient client, string name, CancellationToken ct)
    {
        try
        {
            return await FindAsync(client, name, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is AcapDeviceException or TaskCanceledException or TimeoutException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<InstalledApplication?> FindAsync(ApplicationApiClient client, string name, CancellationToken ct) =>
        (await client.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));

    private async Task<InstalledApplication?> WaitForVersionAsync(ApplicationApiClient client, string name, string version, TimeSpan timeout, CancellationToken ct)
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            try
            {
                var app = await FindAsync(client, name, ct).ConfigureAwait(false);
                if (app is not null && AcapCompatibility.CompareVersions(app.Version, version) == 0)
                {
                    return app;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && timeout > TimeSpan.Zero && ex is TaskCanceledException or TimeoutException or HttpRequestException)
            {
                // The device may still be busy installing; keep polling until the timeout.
            }

            if (_time.GetElapsedTime(start) >= timeout)
            {
                return null;
            }

            await Task.Delay(_verifyInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Synchronous IProgress (Progress&lt;T&gt; would post to the thread pool and reorder reports).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
