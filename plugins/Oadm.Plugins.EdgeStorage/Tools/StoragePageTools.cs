using System.ComponentModel;
using System.Globalization;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeStorage.Tools;

/// <summary>
/// The page's Read as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): reads the SD cards of the chosen cameras of the
/// caller's view, as the page does without asking (read only), with the rights of <see cref="EdgeStorageMethods.Read"/>. The rows
/// follow in <c>oadm_edge_storage_cards</c>. Check, repair and format are the storage task (the task tools).
/// </summary>
internal static class StoragePageTools
{
    public const string ChooseCameras = "Choose cameras, or set all_devices.";

    public static PluginPageTools<EdgeStoragePlugin> Set { get; } = new PluginPageTools<EdgeStoragePlugin>(StorageTools.Json)
        .Read<ReadArgs, ReadAnswer>(
            new("read", "Read SD cards", "Reads the SD cards of the chosen cameras, or of all your cameras with all_devices. It only reads them; the rows follow in oadm_edge_storage_cards.")
            {
                DeviceFilter = true,
            },
            EdgeStorageMethods.Read,
            ReadAsync);

    private static async Task<ReadAnswer> ReadAsync(EdgeStoragePlugin plugin, ICallContext caller, PluginToolInput<ReadArgs> input, CancellationToken ct)
    {
        IReadOnlyList<Guid>? ids = null;
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

            ids = input.Devices.Ids;
        }

        var state = await plugin.StartReadAsync(caller, ids, ct).ConfigureAwait(false);
        return new ReadAnswer(state.Reading, state.Done, state.Total);
    }

    internal sealed record ReadArgs(
        [property: Description("Read all your cameras; the filter is then not used.")] bool AllDevices = false);

    internal sealed record ReadAnswer(
        [property: Description("A read is running.")] bool Reading,
        [property: Description("Cameras read so far.")] int Done,
        [property: Description("Cameras the read covers.")] int Total);
}
