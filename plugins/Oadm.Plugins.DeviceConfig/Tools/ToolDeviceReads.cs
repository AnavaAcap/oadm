using System.Collections.Concurrent;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Tools;

/// <summary>What one device answered a tool's read: its settings, or why it could not be read.</summary>
internal sealed record DeviceRead(IDeviceInfo Device, DeviceSettings Settings)
{
    public bool Ok => Settings.Error is null;
}

/// <summary>The reads of a tool: the devices read (in the order asked) and the devices not read in time or beyond the limit.</summary>
internal sealed record ReadRound(IReadOnlyList<DeviceRead> Reads, int Devices, int NotRead);

/// <summary>
/// Reads many devices for a tool with the code of the dialog (<see cref="DeviceSettingsReader"/>): only devices of the caller's
/// view (each id looked up with the caller; one it cannot see refuses the call, fail closed), at most <see cref="Parallelism"/> at a
/// time like the export, and within <see cref="Budget"/>: what is not read by then counts as not read, so a call over many
/// devices answers in time with what it has. Read-only.
/// </summary>
internal static class ToolDeviceReads
{
    /// <summary>Devices read at the same time (the export reads 8).</summary>
    public const int Parallelism = DeviceConfigIds.ExportParallelism;

    /// <summary>The longest a tool reads devices before it answers with what it has (an assistant's answer may take 120 s in all).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);

    public static async Task<ReadRound> ReadAsync(
        ICallContext caller,
        PluginToolDevices devices,
        Func<IVapixClient, IDeviceInfo, CancellationToken, Task<DeviceSettings>> read,
        TimeProvider time,
        CancellationToken ct,
        TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(time);

        // An id the caller cannot see refuses the whole call before any device is asked, exactly like an unknown id (the host's
        // NOT_FOUND "1 device is no longer available."): the host hands only ids of the caller's view.
        var found = new List<IDeviceInfo>(devices.Ids.Count);
        var gone = 0;
        foreach (var id in devices.Ids)
        {
            if (await caller.Devices.FindAsync(id, ct).ConfigureAwait(false) is { } device)
            {
                found.Add(device);
            }
            else
            {
                gone++;
            }
        }

        if (gone > 0)
        {
            throw new DevicesNotAvailableException(gone);
        }

        var results = new ConcurrentDictionary<Guid, DeviceRead>();
        using var bound = new CancellationTokenSource(budget ?? Budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
        try
        {
            await Parallel.ForEachAsync(found, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = linked.Token }, async (device, token) =>
            {
                DeviceSettings settings;
                try
                {
                    var vapix = await caller.Vapix.CreateAsync(device.Id, token).ConfigureAwait(false);
                    settings = await read(vapix, device, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    settings = new DeviceSettings
                    {
                        Error = ex is DevicesNotAvailableException ? DeviceMessages.Removed : DeviceErrors.Describe(ex, timeout: null),
                    };
                }

                results[device.Id] = new DeviceRead(device, settings);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (bound.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The budget is spent: answer with the devices read so far.
        }

        var reads = found.Where(d => results.ContainsKey(d.Id)).Select(d => results[d.Id]).ToList();
        return new ReadRound(reads, devices.Total, found.Count - reads.Count + devices.Left);
    }
}
