using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Network;

/// <summary>
/// What both network tasks tell the tools (docs/spec/mcp.md "SDK"): the dialog's rules at the payload's JSON fields, and the
/// plan of a run from the cached device data with the dialog's reachability warnings.
/// </summary>
internal static class NetworkMcp
{
    /// <summary>The problems of a payload: the dialog's texts, each at the JSON field its text starts with.</summary>
    public static IReadOnlyList<PayloadError> Errors(string? payloadJson, Func<NetworkPayload, IReadOnlyList<string>> validate)
    {
        NetworkPayload payload;
        try
        {
            payload = NetworkPayload.Parse(payloadJson);
        }
        catch (NetworkValidationException ex)
        {
            return [new(string.Empty, string.Join(" ", ex.Errors))];
        }

        return [.. validate(payload).Select(e => new PayloadError(FieldOf(e), e))];
    }

    /// <summary>The JSON field of a dialog text ("IPv4: ..." is <c>ipv4</c>, "No devices selected." is <c>devices</c>).</summary>
    internal static string FieldOf(string message) => message switch
    {
        _ when message.StartsWith("IPv4:", StringComparison.Ordinal) => "ipv4",
        _ when message.StartsWith("IPv6:", StringComparison.Ordinal) => "ipv6",
        _ when message.StartsWith("DNS:", StringComparison.Ordinal) => "dns",
        _ when message.StartsWith("Host name:", StringComparison.Ordinal) => "hostName",
        _ when message.StartsWith("No devices", StringComparison.Ordinal) => "devices",
        _ => string.Empty,
    };

    /// <summary>
    /// A device runs when the task can run on it and the payload lists it (the dialog resolves one address per device); the
    /// warnings are the dialog's confirmation texts. The address of a device is checked free when its task runs.
    /// </summary>
    public static TaskPlan Plan(ITaskPlugin plugin, NetworkPayload payload, IReadOnlyList<IDeviceInfo> devices)
    {
        var planned = new List<TaskPlanDevice>(devices.Count);
        var moving = 0;
        foreach (var device in devices)
        {
            if (!plugin.CanRun(device))
            {
                planned.Add(new(device.Id, plugin.NotSupportedReason(device) ?? TaskSupportReasons.Default));
            }
            else if (!payload.Devices.TryGetValue(device.Id, out var entry))
            {
                planned.Add(new(device.Id, "The settings do not list this device."));
            }
            else
            {
                planned.Add(new(device.Id));
                if (payload.Ipv4 is { Mode: Ipv4Mode.Static } && !string.IsNullOrWhiteSpace(entry.Ipv4Address)
                    && !string.Equals(device.Address, entry.Ipv4Address.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    moving++;
                }
            }
        }

        var warnings = new List<string>();
        var running = planned.Count(p => p.SkipReason is null);
        if (payload.Ipv4 is { Mode: Ipv4Mode.Dhcp })
        {
            warnings.Add(NetworkWarningTexts.Dhcp);
        }
        else if (payload.Ipv4 is not null)
        {
            if (moving > 0)
            {
                warnings.Add(NetworkWarningTexts.Moving(moving, running));
            }

            warnings.Add(NetworkWarningTexts.MaskAndRouter);
        }

        return new TaskPlan(planned) { Warnings = warnings };
    }
}
