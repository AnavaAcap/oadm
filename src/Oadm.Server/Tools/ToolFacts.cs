using Oadm.Contracts.Vulnerabilities;
using Oadm.Server.Auth;
using Oadm.Server.Vulnerabilities;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>
/// What the device rows of the tools add to a device's record, as the device grid shows it: the server's clock (a
/// certificate's days left) and the CVE list through <c>VulnerabilityService.Get</c> as the caller, matched with the shared
/// matcher, the same as the client's Vulnerabilities column. The parsed list is kept until the server has a newer one.
/// </summary>
internal sealed class ToolFacts(TimeProvider time)
{
    public static readonly string VulnerabilitiesPath = InProcessCalls.PathOf(Proto.VulnerabilityService.Descriptor, "Get");

    private readonly Lock _gate = new();
    private (string Key, VulnerabilityMatcher Matcher)? _last;

    public DateTime NowUtc => time.GetUtcNow().UtcDateTime;

    /// <summary>The server's CVE list as the caller reads it, with its matcher.</summary>
    public async Task<(Proto.VulnerabilityListReply Reply, VulnerabilityMatcher Matcher)> VulnerabilitiesAsync(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var reply = await call.CallAsync<VulnerabilityGrpcService, Proto.Empty, Proto.VulnerabilityListReply>(VulnerabilitiesPath, new Proto.Empty(),
            (s, r, c) => s.Get(r, c)).ConfigureAwait(false);
        var key = string.Concat(reply.Updated?.ToString() ?? string.Empty, "|", reply.Source, "|", reply.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "|", reply.ListJson.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        lock (_gate)
        {
            if (_last is { } last && last.Key == key)
            {
                return (reply, last.Matcher);
            }
        }

        var matcher = new VulnerabilityMatcher(VulnerabilityList.Parse(reply.ListJson));
        lock (_gate)
        {
            _last = (key, matcher);
        }

        return (reply, matcher);
    }

    /// <summary>The row facts of a page: the vulnerabilities only when a field asks for them.</summary>
    public async Task<RowFacts> RowFactsAsync(ToolCall call, bool withVulnerabilities) =>
        new(NowUtc, withVulnerabilities ? (await VulnerabilitiesAsync(call).ConfigureAwait(false)).Matcher : null);
}

/// <summary>What a device row needs besides the device: the server's clock and the CVE matcher (null: not asked for).</summary>
internal sealed record RowFacts(DateTime NowUtc, VulnerabilityMatcher? Vulnerabilities);
