using System.Collections.Concurrent;

using Oadm.Contracts.Vulnerabilities;
using Oadm.Core.Devices;
using Oadm.Core.Vapix;
using Oadm.Server.Vulnerabilities;

namespace Oadm.Server.Devices;

/// <summary>
/// What OADM knows of a device besides its record, as the device grid shows it (docs/spec/mcp.md "Devices and the device
/// filter"): the clock against the allowed difference of now, the Axis CVEs of its AXIS OS (the same matcher and inputs as the
/// client's Vulnerabilities column), the certificate's end against now and its warning days. One per request: the device
/// filter and the counts of <c>DeviceService.Search</c> read it; nothing is asked of a device. Thread safe.
/// </summary>
/// <param name="ClockToleranceSeconds">Polling.ClockToleranceSeconds now.</param>
/// <param name="Vulnerabilities">The matcher of the server's CVE list.</param>
/// <param name="NowUtc">The server's clock for the certificate's days left.</param>
/// <param name="CertWarningDays">The days of PKI's Expiry warning policy of a device (30 without one).</param>
public sealed record DeviceFacts(int ClockToleranceSeconds, VulnerabilityMatcher Vulnerabilities, DateTime NowUtc, Func<Device, int> CertWarningDays)
{
    private readonly ConcurrentDictionary<(string Model, string Firmware, string Soc), DeviceVulnerabilities> _vulnerabilities = new();

    /// <summary>The CVEs of the device's model, AXIS OS and chip, as the Vulnerabilities column counts them (shared per key).</summary>
    public DeviceVulnerabilities VulnerabilitiesOf(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.FirmwareVersion))
        {
            return DeviceVulnerabilities.None;
        }

        return _vulnerabilities.GetOrAdd((device.Model ?? string.Empty, device.FirmwareVersion, device.Soc ?? string.Empty), key =>
        {
            var items = Vulnerabilities.Match(key.Model, key.Firmware, key.Soc);
            return items.Count == 0 ? DeviceVulnerabilities.None : new DeviceVulnerabilities(items);
        });
    }

    /// <summary>The Clock column's warning: status Ok and beyond the allowed difference or NTP not synced.</summary>
    public bool ClockOff(Device device) => ClockHealth.IsOff(device, ClockToleranceSeconds);
}

/// <summary>Makes the <see cref="DeviceFacts"/> of a request from the services that hold them.</summary>
public sealed class DeviceFactsSource(ClockTolerance clock, VulnerabilityStore vulnerabilities, TrustAnchorRegistry anchors, TimeProvider time)
{
    public DeviceFacts Now() => new(clock.Seconds, vulnerabilities.Matcher, time.GetUtcNow().UtcDateTime, anchors.ExpiryWarningDays);
}
