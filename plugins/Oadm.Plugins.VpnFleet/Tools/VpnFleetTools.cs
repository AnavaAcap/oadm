using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VpnFleet.Tools;

/// <summary>
/// The page as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15), each with the rights of its page method: the table,
/// a read of the chosen devices, the newest releases, one camera's settings (never the value of a secret), and the changes
/// install or upgrade and save settings as preview and run. Setting up many cameras with keys stays on the page.
/// </summary>
internal static class VpnFleetTools
{
    public const string ChooseDevices = "Choose devices, or set all_devices.";

    public static PluginPageTools<VpnFleetPlugin> Set { get; } = new PluginPageTools<VpnFleetPlugin>()
        .Read<ResultsArgs, ResultsAnswer>(
            new("results", "VPN apps of the cameras", "Lists the VPN apps the last read found on the cameras: app, status, tunnel address, version and newer version. Start a read with oadm_vpn_fleet_read.")
            {
                Paged = true,
            },
            Fleet.GetState,
            ResultsAsync)
        .Read<ReadArgs, ReadAnswer>(
            new("read", "Read the VPN apps", "Reads the VPN apps of the chosen cameras, or all your cameras with all_devices, as the VPN Fleet page does. It only reads them; the rows follow in oadm_vpn_fleet_results.")
            {
                DeviceFilter = true,
            },
            Fleet.Refresh,
            ReadAsync)
        .Read<ReleasesArgs, ReleasesAnswer>(
            new("releases", "Newest VPN app versions", "The newest release of each VPN app on GitHub (github.com/Mo3he)."),
            Fleet.GetReleases,
            ReleasesAsync)
        .Read<SettingsArgs, SettingsAnswer>(
            new("settings", "Settings of a camera's VPN app", "The settings of one VPN app on one camera. Secrets (keys, passwords) show only whether they are set."),
            Fleet.GetSettings,
            SettingsAsync)
        .Change<InstallArgs, InstallPlan, InstallResult>(
            new("install", "Install or upgrade a VPN app", "Installs the newest release of a VPN app from GitHub on the chosen cameras, or upgrades it. A camera that has that version already is not changed.")
            {
                DeviceFilter = true,
            },
            Fleet.Install,
            CheckInstallAsync,
            RunInstallAsync)
        .Change<SaveArgs, SavePlan, SaveResult>(
            new("settings_save", "Change a camera's VPN settings", "Changes settings of one VPN app on one camera. Secrets (keys, passwords) are changed on the VPN Fleet page only."),
            Fleet.SaveSettings,
            CheckSaveAsync,
            RunSaveAsync);

    // ---- Reads ----

    private static Task<ResultsAnswer> ResultsAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<ResultsArgs> input, CancellationToken ct)
    {
        var state = plugin.StateFor(caller);
        var all = state.Rows
            .Where(r => input.Args.App is not { } app || r.Vpn == app)
            .Where(r => !input.Args.UpdatesOnly || r.UpdateAvailable)
            .Select(r => (Key: r.DeviceId.ToString("N", CultureInfo.InvariantCulture) + "/" + (r.Vpn?.ToString() ?? ""), Row: r))
            .OrderBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
        var page = input.Page;
        var rows = all.Where(r => page.Includes(r.Key)).Select(r => (r.Key, Row: RowOf(r.Row))).ToList();
        var (taken, more) = page.Take(rows.Select(r => r.Row), Set.Json);
        var next = more && taken.Count > 0 ? rows[taken.Count - 1].Key : null;
        return Task.FromResult(new ResultsAnswer(
            state.Reading,
            all.Count,
            state.Rows.Count(r => r.UpdateAvailable),
            state.ReleaseError,
            state.Rows.Count == 0 && !state.Reading ? "Nothing read yet. Read the cameras with oadm_vpn_fleet_read." : null,
            taken,
            next));
    }

    private static async Task<ReadAnswer> ReadAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<ReadArgs> input, CancellationToken ct)
    {
        plugin.StartRead(caller, await DevicesAsync(caller, input.Args.AllDevices, input.Devices, ct).ConfigureAwait(false));
        return new ReadAnswer(true);
    }

    private static async Task<ReleasesAnswer> ReleasesAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<ReleasesArgs> input, CancellationToken ct) =>
        new([.. (await plugin.Catalog.GetAllAsync(input.Args.CheckNow, ct).ConfigureAwait(false))
            .Select(r => new ReleaseRow(r.Kind, VpnApps.NameOf(r.Kind), r.Version, r.PageUrl, r.Error))]);

    private static async Task<SettingsAnswer> SettingsAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<SettingsArgs> input, CancellationToken ct)
    {
        var reply = await VpnFleetPlugin.GetSettingsAsync(caller, new SettingsRequest(DeviceId(input.Args.DeviceId), input.Args.App), ct).ConfigureAwait(false);
        return new SettingsAnswer(
            [.. reply.Fields.Select(f => new SettingRow(f.Name, f.Label, f.Kind, f.Kind == SettingKind.Secret ? null : f.Value, f.HasValue, [.. f.Options.Select(o => o.Value)]))],
            reply.PublicKey);
    }

    // ---- Changes ----

    private static async Task<InstallPlan> CheckInstallAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<InstallArgs> input, CancellationToken ct)
    {
        var devices = await DevicesAsync(caller, input.Args.AllDevices, input.Devices, ct).ConfigureAwait(false);
        var (chosen, version) = await plugin.PlanInstallAsync(caller, input.Args.App, [.. devices.Select(d => d.Id)], ct).ConfigureAwait(false);
        return new InstallPlan(chosen.Count, "device", VpnApps.NameOf(input.Args.App), version);
    }

    private static async Task<InstallResult> RunInstallAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<InstallArgs> input, InstallPlan plan, CancellationToken ct)
    {
        var devices = await DevicesAsync(caller, input.Args.AllDevices, input.Devices, ct).ConfigureAwait(false);
        var reply = await VpnFleetPlugin.RunInstallAsync(caller, input.Args.App, devices, plan.Version, upgrade: false, ct).ConfigureAwait(false);
        return new InstallResult(reply.Devices, reply.Version, [.. reply.TaskIds.Select(t => t.ToString())]);
    }

    private static async Task<SavePlan> CheckSaveAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<SaveArgs> input, CancellationToken ct)
    {
        var secrets = input.Args.Values.Keys.Where(k => AppSettings.IsSecret(input.Args.App, k)).ToList();
        if (secrets.Count > 0)
        {
            throw new PluginToolInputException("values", $"{string.Join(", ", secrets)}: change secrets on the VPN Fleet page.");
        }

        var change = await VpnFleetPlugin.CheckSaveAsync(caller, new SaveSettingsRequest(DeviceId(input.Args.DeviceId), input.Args.App, input.Args.Values), ct).ConfigureAwait(false);
        return new SavePlan(change.Changes.Count, "setting", change.Device.Address, [.. change.Changes.Select(c => $"{c.Key} = {c.Value}")]);
    }

    private static async Task<SaveResult> RunSaveAsync(VpnFleetPlugin plugin, ICallContext caller, PluginToolInput<SaveArgs> input, SavePlan plan, CancellationToken ct)
    {
        var change = await VpnFleetPlugin.CheckSaveAsync(caller, new SaveSettingsRequest(DeviceId(input.Args.DeviceId), input.Args.App, input.Args.Values), ct).ConfigureAwait(false);
        var reply = await plugin.ApplySaveAsync(caller, change, ct, ct).ConfigureAwait(false);
        return new SaveResult(reply.Changed, reply.Note);
    }

    // ---- Helpers ----

    /// <summary>The devices of a tool: all of the caller's, or the filter's (at most 500), each looked up with the caller.</summary>
    private static async Task<IReadOnlyList<IDeviceInfo>> DevicesAsync(ICallContext caller, bool all, PluginToolDevices devices, CancellationToken ct)
    {
        if (all)
        {
            return await caller.Devices.ListAsync(ct).ConfigureAwait(false);
        }

        if (devices.Ids.Count == 0)
        {
            throw new PluginToolInputException("filter", ChooseDevices);
        }

        if (devices.Left > 0)
        {
            throw new PluginToolInputException("filter", string.Create(CultureInfo.InvariantCulture, $"Choose at most {PluginTool.MaxDevices} devices, or set all_devices."));
        }

        return await VpnFleetPlugin.FindAsync(caller, devices.Ids, ct).ConfigureAwait(false);
    }

    private static Guid DeviceId(string text) =>
        Guid.TryParse(text, out var id) ? id : throw new PluginToolInputException("device_id", "Enter the id of a device.");

    private static ResultRow RowOf(FleetRow r) => new(
        r.DeviceId.ToString(),
        r.Address,
        r.Model,
        r.Vpn,
        r.Error is null ? r.State : null,
        r.VpnAddress,
        r.AppVersion,
        r.UpdateAvailable ? r.LatestVersion : null,
        r.Error ?? r.Detail);

    // ---- Arguments and answers ----

    internal sealed record ResultsArgs(
        [property: Description("Only rows of this app.")] VpnKind? App = null,
        [property: Description("Only apps with a newer version on GitHub.")] bool UpdatesOnly = false);

    internal sealed record ResultsAnswer(
        [property: Description("A read is running.")] bool Reading,
        [property: Description("Rows that match.")] int Total,
        [property: Description("Apps with a newer version.")] int Updates,
        [property: Description("Why the newest versions are not known.")] string? ReleaseError,
        [property: Description("What to do when there are no rows.")] string? Note,
        [property: Description("The rows of this page.")] IReadOnlyList<ResultRow> Rows,
        [property: Description("Pass as cursor for the next page.")] string? NextCursor);

    internal sealed record ResultRow(
        [property: Description("The device.")] string DeviceId,
        [property: Description("The device's address.")] string Address,
        [property: Description("The device's model.")] string Model,
        [property: Description("The VPN app; empty for a device without one.")] VpnKind? App,
        [property: Description("What the app reports; empty when the device was not read.")] VpnState? State,
        [property: Description("The camera's address in the VPN.")] string? VpnAddress,
        [property: Description("The installed version.")] string? Version,
        [property: Description("A newer version on GitHub.")] string? Update,
        [property: Description("Details, or why the device was not read.")] string? Detail);

    internal sealed record ReadArgs(
        [property: Description("Read all your devices; the filter is then not used.")] bool AllDevices = false);

    internal sealed record ReadAnswer([property: Description("The read started.")] bool Started);

    internal sealed record ReleasesArgs(
        [property: Description("Ask GitHub now instead of the answer of the last 30 minutes.")] bool CheckNow = false);

    internal sealed record ReleasesAnswer([property: Description("One row per app.")] IReadOnlyList<ReleaseRow> Apps);

    internal sealed record ReleaseRow(
        [property: Description("The app.")] VpnKind App,
        [property: Description("Its name.")] string Name,
        [property: Description("The newest version.")] string? Version,
        [property: Description("The release page.")] string? Url,
        [property: Description("Why GitHub could not be read.")] string? Error);

    internal sealed record SettingsArgs(
        [property: Required, Description("The device.")] string DeviceId,
        [property: Description("The VPN app.")] VpnKind App);

    internal sealed record SettingsAnswer(
        [property: Description("The settings.")] IReadOnlyList<SettingRow> Settings,
        [property: Description("WireGuard: the public key of the camera.")] string? PublicKey);

    internal sealed record SettingRow(
        [property: Description("The setting, as the values of settings_save name it.")] string Name,
        [property: Description("What it is.")] string Label,
        [property: Description("Text, secret, bool or choice.")] SettingKind Kind,
        [property: Description("Its value; empty for a secret.")] string? Value,
        [property: Description("A value is set.")] bool HasValue,
        [property: Description("Bool: the on and off values. Choice: the values.")] IReadOnlyList<string> Options);

    internal sealed record InstallArgs(
        [property: Description("The VPN app.")] VpnKind App,
        [property: Description("All your devices; the filter is then not used.")] bool AllDevices = false);

    internal sealed record InstallPlan(int WillChange, string Unit,
        [property: Description("The app.")] string App,
        [property: Description("The version that is installed.")] string Version) : PluginToolPlan(WillChange, Unit);

    internal sealed record InstallResult(
        [property: Description("Devices with a task.")] int Devices,
        [property: Description("The version.")] string Version,
        [property: Description("The tasks, one per device.")] IReadOnlyList<string> TaskIds);

    internal sealed record SaveArgs(
        [property: Required, Description("The device.")] string DeviceId,
        [property: Description("The VPN app.")] VpnKind App,
        [property: Required, Description("The settings to change, by name (see oadm_vpn_fleet_settings), and their new values.")] IReadOnlyDictionary<string, string> Values);

    internal sealed record SavePlan(int WillChange, string Unit,
        [property: Description("The device.")] string Device,
        [property: Description("The changes.")] IReadOnlyList<string> Changes) : PluginToolPlan(WillChange, Unit);

    internal sealed record SaveResult(
        [property: Description("Settings saved.")] int Changed,
        [property: Description("What else to do.")] string? Note);
}
