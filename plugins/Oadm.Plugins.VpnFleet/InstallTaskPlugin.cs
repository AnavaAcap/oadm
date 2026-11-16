using System.Text.Json;

using Oadm.Plugins.Acap;
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
/// Installs or upgrades one app on one device from its newest GitHub release: chooses the package for the device's
/// architecture and AXIS OS, downloads it (once for all devices), installs it, starts the app and checks the version.
/// A device that has the version already is not changed. Started only from the page.
/// </summary>
public sealed class InstallTaskPlugin : ITaskPlugin
{
    public const string PluginId = Fleet.PluginId + ".install";

    public const string CheckStep = "Check the device";
    public const string DownloadStep = "Download the package";
    public const string InstallStep = "Install the package";
    public const string StartStep = "Start the app";
    public const string VerifyStep = "Check the installed version";

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

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return DeviceMessages.Refusal(device) is null;
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        var payload = JsonSerializer.Deserialize<InstallPayload>(payloadJson ?? "null", Fleet.Json)
            ?? throw new ArgumentException("The task has no app. Nothing was changed.");
        var app = VpnApps.Of(payload.Kind);
        var local = payload.Files is { Count: > 0 };
        ctx.PlanSteps(CheckStep, DownloadStep, InstallStep, StartStep, VerifyStep);

        // GitHub: the newest release; uploaded packages: read here, before anything is changed.
        AppRelease? release = null;
        IReadOnlyList<AcapBuild> builds = [];
        if (local)
        {
            builds = await LocalPackages.ReadAsync(ctx.Files, app, payload.Files, ct).ConfigureAwait(false);
        }
        else
        {
            (release, var error) = await _catalog().GetAsync(payload.Kind, checkNow: false, ct).ConfigureAwait(false);
            if (release is null)
            {
                throw new InvalidOperationException((error ?? "The release could not be read.") + " Nothing was changed.");
            }
        }

        var (package, installed) = await ctx.StepAsync(CheckStep, async step =>
        {
            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var apps = await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var current = apps.FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase));
            var chosen = local
                ? ChooseUploaded(builds, device, info)
                : ChooseRelease(release!, info.Architecture ?? device.Architecture, info.Version);
            step.Complete($"{info.Architecture}, AXIS OS {info.Version}" + (current?.Version is { } v ? $", {app.DisplayName} {v}" : ""));
            return (chosen, current);
        }).ConfigureAwait(false);

        if (installed?.Version is { } have && !AppVersions.IsNewer(package.Version, have))
        {
            var why = AppVersions.Compare(have, package.Version) == 0 ? $"{app.DisplayName} {have} is installed already." : $"{app.DisplayName} {have} is newer than {package.Version}.";
            foreach (var name in new[] { DownloadStep, InstallStep, StartStep, VerifyStep })
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

        var after = (await DeviceVpn.ListAppsAsync(ctx.Vapix, ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.Name, app.AppName, StringComparison.OrdinalIgnoreCase));
        if (after is { IsRunning: false })
        {
            await ctx.StepAsync(StartStep, _ => DeviceVpn.StartAppAsync(ctx.Vapix, app.AppName, ct)).ConfigureAwait(false);
        }
        else
        {
            ctx.SkipStep(StartStep, "The app runs already.");
        }

        using var verify = ctx.BeginStep(VerifyStep);
        if (after?.Version is { } now && AppVersions.Compare(now, package.Version) == 0)
        {
            verify.Complete($"{app.DisplayName} {now}");
            ctx.Log(TaskLogLevel.Info, $"{app.DisplayName} {now} is installed ({package.Name}).");
            if (installed is null)
            {
                ctx.Log(TaskLogLevel.Info, "Set up the VPN on the VPN Fleet page.");
            }
        }
        else
        {
            verify.Warn($"The device reports version {after?.Version ?? "none"}, expected {package.Version}.");
        }
    }

    /// <summary>The package of one device: a GitHub file (<see cref="Asset"/>) or an upload (<see cref="FileId"/>).</summary>
    private sealed record ChosenPackage(string Name, string Version, ReleaseAsset? Asset, string? FileId);

    private static ChosenPackage ChooseRelease(AppRelease release, string? architecture, string? axisOs)
    {
        var (asset, reason) = ReleaseCatalog.ChooseAsset(release, architecture, axisOs);
        return asset is null
            ? throw new VpnDeviceException(reason + " Nothing was changed.")
            : new ChosenPackage(asset.Name, release.Version, asset, null);
    }

    /// <summary>The uploaded build that fits the device, chosen like Applications (ACAP) does: architecture, AXIS OS, root.</summary>
    private static ChosenPackage ChooseUploaded(IReadOnlyList<AcapBuild> builds, IDeviceInfo device, BasicDeviceInfo info)
    {
        var group = new AcapDeviceGroup(device.Model ?? info.ProdNbr, info.Version, info.Architecture ?? device.Architecture, info.Soc ?? device.Soc);
        var choice = AcapBuildChooser.Choose(builds, group);
        if (choice.Pick != AcapPick.Chosen || choice.Index < 0)
        {
            throw new VpnDeviceException((choice.Reason ?? "No package fits this device.") + " Nothing was changed.");
        }

        // A single package is always "chosen"; whether it fits is the caller's check.
        var build = builds[choice.Index];
        return AcapBuildChooser.Misfit(build.Package, group) is { } why
            ? throw new VpnDeviceException($"{build.Name} does not fit this device: {why}. Nothing was changed.")
            : new ChosenPackage(build.Name, build.Package.Version, null, build.Key);
    }
}
