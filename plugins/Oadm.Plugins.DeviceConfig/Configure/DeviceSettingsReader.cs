using System.Collections.Concurrent;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Configure;

/// <summary>
/// Reads what Configure devices and Create configuration file show of one device: its parameters (definitions and values),
/// and for the source device also its action rules, schedules and stream profiles. A part that cannot be read is a
/// problem of that part only. The tools (<see cref="Tools.ConfigTools"/>) read single parts of many devices with the same
/// code. Read-only.
/// </summary>
public static class DeviceSettingsReader
{
    /// <summary>How long definitions of a model and AXIS OS are kept (they never change for the same AXIS OS).</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, (DateTime Until, IReadOnlyList<ParamDefinition> Definitions)> Cache = new(StringComparer.Ordinal);

    /// <summary>Answers the read (<paramref name="full"/>) or definitions query.</summary>
    public static Task<DeviceSettings> ReadAsync(ITaskQueryContext ctx, IDeviceInfo device, bool full, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ReadAsync(ctx.Vapix, device, full, ct);
    }

    /// <summary>The read (<paramref name="full"/>) or definitions query over a client of the device.</summary>
    public static Task<DeviceSettings> ReadAsync(IVapixClient vapix, IDeviceInfo device, bool full, CancellationToken ct) =>
        GuardedAsync(vapix, device, async () =>
        {
            var info = await vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var key = KeyOf(info);
            IReadOnlyList<ParamDefinition> parameters;
            if (!full && Cached(key) is { } cached)
            {
                parameters = cached;
            }
            else
            {
                parameters = await ParamDefinitionsReader.ReadAsync(vapix, ct).ConfigureAwait(false);
                Remember(key, parameters);
                if (!full)
                {
                    parameters = Cached(key)!;
                }
            }

            var settings = new DeviceSettings { Model = info.ProdNbr, Firmware = info.Version, Serial = info.SerialNumber, Parameters = parameters };
            if (!full)
            {
                return settings;
            }

            var problems = new List<string>();
            IReadOnlyList<ConfigActionRule> rules = [];
            IReadOnlyList<ConfigSchedule> schedules = [];
            IReadOnlyList<ConfigStreamProfile> profiles = [];
            try
            {
                rules = await ActionEngineApi.ReadRulesAsync(vapix, ct).ConfigureAwait(false);
                schedules = rules.Count == 0 ? [] : await ActionEngineApi.ReadSchedulesAsync(vapix, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                problems.Add("Action rules could not be read: " + DeviceErrors.Describe(ex, timeout: null));
            }

            if (device.HasVideo)
            {
                try
                {
                    profiles = (await StreamProfileApi.ListAsync(vapix, ct).ConfigureAwait(false)).Profiles;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    problems.Add("Stream profiles could not be read: " + DeviceErrors.Describe(ex, timeout: null));
                }
            }

            return settings with { Rules = rules, Schedules = schedules, StreamProfiles = profiles, Problems = problems };
        }, ct);

    /// <summary>
    /// The parameters of the device that <paramref name="match"/> takes, with their values. Exact <paramref name="names"/> read only
    /// their groups; a search reads only the groups of the matching names when the definitions of the model and AXIS OS are
    /// known (<see cref="CacheFor"/>), else every parameter once (and keeps the definitions).
    /// </summary>
    public static Task<DeviceSettings> ReadParametersAsync(IVapixClient vapix, IDeviceInfo device, Func<ParamDefinition, bool> match, IReadOnlyCollection<string>? names, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(match);
        return GuardedAsync(vapix, device, async () =>
        {
            var info = await vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            var key = KeyOf(info);
            IReadOnlyList<ParamDefinition> read;
            if (names is { Count: > 0 })
            {
                read = await ParamDefinitionsReader.ReadAsync(vapix, ct, names).ConfigureAwait(false);
            }
            else if (Cached(key) is { } cached)
            {
                var wanted = cached.Where(match).Select(p => p.Name).ToList();
                read = wanted.Count == 0 ? [] : await ParamDefinitionsReader.ReadAsync(vapix, ct, wanted).ConfigureAwait(false);
            }
            else
            {
                read = await ParamDefinitionsReader.ReadAsync(vapix, ct).ConfigureAwait(false);
                Remember(key, read);
            }

            return new DeviceSettings { Model = info.ProdNbr, Firmware = info.Version, Serial = info.SerialNumber, Parameters = [.. read.Where(match)] };
        }, ct);
    }

    /// <summary>The device's action rules with their actions, and its schedules when a rule names one.</summary>
    public static Task<DeviceSettings> ReadRulesAsync(IVapixClient vapix, IDeviceInfo device, CancellationToken ct) =>
        GuardedAsync(vapix, device, async () =>
        {
            var rules = await ActionEngineApi.ReadRulesAsync(vapix, ct).ConfigureAwait(false);
            var schedules = rules.Any(r => r.ScheduleIds.Any()) ? await ActionEngineApi.ReadSchedulesAsync(vapix, ct).ConfigureAwait(false) : [];
            return new DeviceSettings { Rules = rules, Schedules = schedules };
        }, ct);

    /// <summary>The device's stream profiles.</summary>
    public static Task<DeviceSettings> ReadStreamProfilesAsync(IVapixClient vapix, IDeviceInfo device, CancellationToken ct) =>
        GuardedAsync(vapix, device, async () =>
            new DeviceSettings { StreamProfiles = (await StreamProfileApi.ListAsync(vapix, ct).ConfigureAwait(false)).Profiles }, ct);

    /// <summary>
    /// A read of a device that refuses without a request for a status the device cannot answer in (Unreachable, Site offline,
    /// ...) and answers a failure as <see cref="DeviceSettings.Error"/> in the shared device texts; a cancel of the caller ends it.
    /// </summary>
    private static async Task<DeviceSettings> GuardedAsync(IVapixClient vapix, IDeviceInfo device, Func<Task<DeviceSettings>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(device);
        if (TaskSupportReasons.ForStatus(device) is { } refused)
        {
            return new DeviceSettings { Error = refused };
        }

        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (DeviceErrors.IsCertificateChanged(ex))
            {
                return new DeviceSettings { Error = DeviceMessages.CertificateChanged };
            }

            return new DeviceSettings { Error = DeviceErrors.Describe(ex, timeout: null) };
        }
    }

    private static string KeyOf(BasicDeviceInfo info) => ModelNames.Normalize(info.ProdNbr) + "|" + info.Version;

    private static IReadOnlyList<ParamDefinition>? Cached(string key) =>
        Cache.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow ? cached.Definitions : null;

    private static void Remember(string key, IReadOnlyList<ParamDefinition> parameters) =>
        Cache[key] = (DateTime.UtcNow + CacheFor, [.. parameters.Select(p => p with { Value = null })]);

    /// <summary>Forgets cached definitions (tests).</summary>
    internal static void ClearCache() => Cache.Clear();
}
