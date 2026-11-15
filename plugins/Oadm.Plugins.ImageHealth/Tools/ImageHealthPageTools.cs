using System.ComponentModel;
using System.Globalization;

using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth.Tools;

/// <summary>
/// The page's Check now as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): asks the chosen cameras of the caller's view
/// once for the app status, as the page does without asking (read only for the cameras), with the rights of the page method
/// <see cref="ImageHealthMethods.Check"/>. The rows follow in <c>oadm_image_health_results</c>.
/// </summary>
internal static class ImageHealthPageTools
{
    public const string ChooseCameras = "Choose cameras, or set all_devices.";

    public static PluginPageTools<ImageHealthMonitor> Set { get; } = new PluginPageTools<ImageHealthMonitor>(ImageHealthTools.Json)
        .Read<CheckArgs, CheckAnswer>(
            new("check", "Check image health", "Asks the chosen cameras, or all your cameras with all_devices, for their image health now. It only reads them; read the rows with oadm_image_health_results.")
            {
                DeviceFilter = true,
            },
            ImageHealthMethods.Check,
            CheckAsync);

    private static async Task<CheckAnswer> CheckAsync(ImageHealthMonitor monitor, ICallContext caller, PluginToolInput<CheckArgs> input, CancellationToken ct)
    {
        HashSet<Guid>? ids = null;
        if (!input.Args.AllDevices)
        {
            if (input.Devices.Ids.Count == 0)
            {
                throw new PluginToolInputException("filter", ChooseCameras);
            }

            if (input.Devices.Left > 0)
            {
                throw new PluginToolInputException("filter", string.Create(CultureInfo.InvariantCulture, $"Choose at most {PluginTool.MaxDevices} cameras, or set all_devices."));
            }

            ids = await CallDevices.KnownAsync(caller, input.Devices.Ids, ct).ConfigureAwait(false); // a group user: every id in the view
        }

        monitor.StartCheck(caller.DeviceScopeKey, ids);
        var state = await monitor.SnapshotAsync(caller, ct).ConfigureAwait(false);
        return new CheckAnswer(state.Checking, state.Checked, state.Total);
    }

    internal sealed record CheckArgs(
        [property: Description("Check all your video devices; the filter is then not used.")] bool AllDevices = false);

    internal sealed record CheckAnswer(
        [property: Description("A check is running.")] bool Checking,
        [property: Description("Cameras checked so far.")] int Checked,
        [property: Description("Cameras the check covers.")] int Total);
}
