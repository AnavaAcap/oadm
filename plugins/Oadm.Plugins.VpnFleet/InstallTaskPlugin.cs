using System.Text.Json;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VpnFleet;

/// <summary>One run of <see cref="InstallTaskPlugin"/>: the app and its version; <see cref="Files"/> the uploaded packages, else GitHub.</summary>
public sealed record InstallPayload(VpnKind Kind, string Version, bool Upgrade)
{
    public IReadOnlyList<PackageFile>? Files { get; init; }
}

/// <summary>
/// Installs or upgrades one app on one device with the version the user approved (from its GitHub release or uploaded
/// packages): chooses the package for the device, downloads it (once for all devices), checks it like Applications (ACAP)
/// does with the device's own facts, installs it, starts the app and checks the version. A device that has the version
/// already is not changed. Started only from the page.
/// </summary>
public sealed class InstallTaskPlugin : ITaskPlugin
{
    public const string PluginId = Fleet.PluginId + ".install";

    public const string CheckStep = "Check the device";
    public const string DownloadStep = "Download the package";
    public const string CheckPackageStep = "Check the package";
    public const string InstallStep = "Install the package";
    public const string StartStep = "Start the app";
    public const string VerifyStep = "Check the installed version";

    private static readonly Version MinApplicationApi = Version.Parse(AcapPlugin.ApplicationApiMinVersion);

    private readonly Func<ReleaseCatalog> _catalog;

    internal InstallTaskPlugin(Func<ReleaseCatalog> catalog)
    {
        _catalog = catalog;
    }

    public string Id => PluginId;

    public string DisplayName => "Install a VPN app";

    public string? IconKey => "app";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool ShowInMenus => false;

    /// <summary>Uploads of packages are large: at most 4 devices at a time.</summary>
    public int? MaxParallelDevices => 4;

    public string GetTaskName(string? payloadJson)
    {
        try
        {
            var p = JsonSerializer.Deserialize<InstallPayload>(payloadJson ?? "null", Fleet.Json);
            return p is null ? DisplayName
                : p.Upgrade ? $"Upgrade {VpnApps.NameOf(p.Kind)} to {p.Version}"
                : $"Install {VpnApps.NameOf(p.Kind)} {p.Version}";
        }
        catch (JsonException)
        {
            return DisplayName;
        }
    }

    /// <summary>Reachable with credentials, and the cached API list offers the Application API 1.x.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return DeviceMessages.Refusal(device) is null && CachedApiCheck.Supports(device.Apis, AcapPlugin.ApplicationApiId, MinApplicationApi);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = JsonSerializer.Deserialize<InstallPayload>(payloadJson ?? "null", Fleet.Json)
            ?? throw new ArgumentException("The task has no app. Nothing was changed.");
        var app = VpnApps.Of(payload.Kind);
        var local = payload.Files is { Count: > 0 };
        ctx.PlanSteps(CheckStep, DownloadStep, CheckPackageStep, InstallStep, StartStep, VerifyStep);

        // GitHub: the release of the approved version; uploaded packages: read here, before anything is changed.
        AppRelease? release = null;
        IReadOnlyList<AcapBuild> builds = [];
        if (local)
        {
            builds = await LocalPackages.ReadAsync(ctx.Files, app, payload.Files, ct).ConfigureAwait(false);
            if (AppVersions.Compare(builds[0].Package.Version, payload.Version) != 0)
            {
                throw new ArgumentException($"The packages are version {builds[0].Package.Version}, not the approved {payload.Version}. Nothing was changed.");
            }
        }
        else
        {
            (release, var error) = await _catalog().GetAsync(payload.Kind, checkNow: false, ct).ConfigureAwait(false);
            if (release is null)
            {
                throw new InvalidOperationException((error ?? "The release could not be read.") + " Nothing was changed.");
            }

            if (AppVersions.Compare(release.Version, payload.Version) != 0)
            {
                throw new InvalidOperationException(
                    $"The newest release of {app.DisplayName} is now {release.Version}, not the approved {payload.Version}. Start the install again. Nothing was changed.");
            }
        }

        var client = new ApplicationApiClient(ctx.Vapix);
        var (package, facts, apps, installed) = await ctx.StepAsync(CheckStep, async step =>
        {
            (await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false)).Require(AcapPlugin.ApplicationApiId, AcapPlugin.ApplicationApiMinVersion);
            var facts = await client.GetDeviceFactsAsync(ct).ConfigureAwait(false);
            var apps = await client.ListAsync(ct).ConfigureAwait(false);
            var current = Find(apps, app);
            var chosen = local ? ChooseUploaded(builds, device, facts) : ChooseRelease(release!, facts);
            step.Complete($"{facts.Architecture}, AXIS OS {facts.FirmwareVersion}" + (current?.Version is { } v ? $", {app.DisplayName} {v}" : ""));
            return (chosen, facts, apps, current);
        }).ConfigureAwait(false);

        // An installed version that cannot be read never stops the install.
        if (installed?.Version is { } have && !AppVersions.IsNewer(package.Version, have))
        {
            var why = AppVersions.Compare(have, package.Version) == 0 ? $"{app.DisplayName} {have} is installed already." : $"{app.DisplayName} {have} is newer than {package.Version}.";
            foreach (var name in new[] { DownloadStep, CheckPackageStep, InstallStep, StartStep, VerifyStep })
            {
                ctx.SkipStep(name, why);
            }

            ctx.Log(TaskLogLevel.Info, why + " Nothing was changed.");
            return;
        }

        string? path = null;
        if (package.Asset is { } asset)
        {
            path = await ctx.StepAsync(DownloadStep, async step =>
            {
                var progress = new Progress<long>(bytes => step.ReportProgress(asset.Size > 0 ? (int)(bytes * 100 / asset.Size) : 0));
                var file = await _catalog().DownloadAsync(asset, progress, ct).ConfigureAwait(false);
                step.Complete(asset.Name);
                return file;
            }).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(DownloadStep, "Uploaded from your computer: " + package.Name);
        }

        await ctx.StepAsync(CheckPackageStep, async step =>
        {
            var manifest = package.Manifest ?? await ReadDownloadAsync(path!, package.Name, ct).ConfigureAwait(false);
            var why = Refusal(app, manifest, facts, installed, apps);
            if (why is not null)
            {
                throw new VpnDeviceException($"{package.Name}: {why} Nothing was changed.");
            }

            step.Complete($"{manifest.DisplayName} {manifest.Version}, {manifest.Architecture ?? "any architecture"}");
        }).ConfigureAwait(false);

        await ctx.StepAsync(InstallStep, async step =>
        {
            if (path is not null)
            {
                await DeviceVpn.InstallPackageAsync(ctx.Vapix, path, ct).ConfigureAwait(false);
            }
            else
            {
                var stream = await ctx.Files.OpenReadAsync(package.FileId!, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await DeviceVpn.InstallPackageAsync(ctx.Vapix, stream, package.Name, ct).ConfigureAwait(false);
                }
            }

            step.Complete(installed is null ? $"Installed {package.Version}" : $"Upgraded from {installed.Version}");
        }).ConfigureAwait(false);

        var after = await ctx.StepAsync(StartStep, async step =>
        {
            var now = Find(await client.ListAsync(ct).ConfigureAwait(false), app)
                ?? throw new VpnDeviceException($"The device does not list {app.DisplayName} after the install. Check its apps and its system log.");
            if (IsRunning(now))
            {
                step.Complete("The app runs already.");
            }
            else
            {
                await DeviceVpn.StartAppAsync(ctx.Vapix, app.AppName, ct).ConfigureAwait(false);
                step.Complete("Started");
            }

            return now;
        }).ConfigureAwait(false);

        using var verify = ctx.BeginStep(VerifyStep);
        if (after.Version is { } version && AppVersions.Compare(version, package.Version) == 0)
        {
            verify.Complete($"{app.DisplayName} {version}");
            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} {version} is installed ({package.Name}).");
            if (installed is null)
            {
                ctx.Log(TaskLogLevel.Info, "Set up the VPN on the VPN Fleet page.");
            }
        }
        else
        {
            verify.Warn($"The device reports version {after.Version ?? "none"}, expected {package.Version}.");
        }
    }

    /// <summary>
    /// Why the package must not go on the device, null when it may: another app, or the checks of Applications (ACAP) with the
    /// device's own facts (architecture, AXIS OS of the package's range or schema, root, SDK, a clash of names). The version
    /// is decided before (<see cref="AppVersions"/>), so a downgrade never gets here.
    /// </summary>
    internal static string? Refusal(VpnApp app, EapManifest manifest, AcapDeviceFacts facts, InstalledApplication? installed, IReadOnlyList<InstalledApplication> apps)
    {
        if (!string.Equals(manifest.AppName, app.AppName, StringComparison.OrdinalIgnoreCase))
        {
            return $"this is a package of {manifest.DisplayName}, not {app.DisplayName}.";
        }

        var report = AcapCompatibility.Check(manifest, facts, installed, allowDowngrade: true, apps);
        return report.IsCompatible ? null : string.Join(" ", report.Problems);
    }

    private static InstalledApplication? Find(IReadOnlyList<InstalledApplication> apps, VpnApp app) =>
        apps.FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase));

    private static bool IsRunning(InstalledApplication app) => string.Equals(app.Status, "Running", StringComparison.OrdinalIgnoreCase);

    private static async Task<EapManifest> ReadDownloadAsync(string path, string name, CancellationToken ct)
    {
        try
        {
            return await EapReader.ReadAsync(path, ct).ConfigureAwait(false);
        }
        catch (InvalidEapException ex)
        {
            throw new VpnDeviceException($"{name}: {ex.Message} Nothing was changed.");
        }
    }

    /// <summary>
    /// The package of one device: a GitHub file (<see cref="Asset"/>, read after the download) or an upload
    /// (<see cref="FileId"/> with its <see cref="Manifest"/>).
    /// </summary>
    private sealed record ChosenPackage(string Name, string Version, ReleaseAsset? Asset, string? FileId, EapManifest? Manifest);

    private static ChosenPackage ChooseRelease(AppRelease release, AcapDeviceFacts facts)
    {
        var (asset, reason) = ReleaseCatalog.ChooseAsset(release, facts.Architecture, facts.FirmwareVersion);
        return asset is null
            ? throw new VpnDeviceException(reason + " Nothing was changed.")
            : new ChosenPackage(asset.Name, release.Version, asset, null, null);
    }

    /// <summary>The uploaded build that fits the device, chosen like Applications (ACAP) does: architecture, AXIS OS, root, SDK.</summary>
    private static ChosenPackage ChooseUploaded(IReadOnlyList<AcapBuild> builds, IDeviceInfo device, AcapDeviceFacts facts)
    {
        var group = AcapDeviceGroup.Of(facts with { Architecture = facts.Architecture ?? device.Architecture, Soc = facts.Soc ?? device.Soc }, device.Model);
        var choice = LocalPackages.Choose(builds, group, facts);
        if (choice.Pick != AcapPick.Chosen || choice.Index < 0)
        {
            throw new VpnDeviceException((choice.Reason ?? "No package fits this device.") + " Nothing was changed.");
        }

        // A single package is always "chosen"; whether it fits is checked here and again in Check the package.
        var build = builds[choice.Index];
        return AcapBuildChooser.Misfit(build.Package, group, facts) is { } why
            ? throw new VpnDeviceException($"{build.Name} does not fit this device: {why}. Nothing was changed.")
            : new ChosenPackage(build.Name, build.Package.Version, null, build.Key, build.Package);
    }
}
