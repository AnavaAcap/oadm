using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Grpc.Core;

using Oadm.Contracts.Text;
using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Compliance;
using Oadm.Server.Devices;

using static Oadm.Server.Tools.ToolSchema;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>
/// The CSV exports of the host as files (docs/spec/mcp.md "Files"): the device list (the columns of Export devices the server
/// knows, the permission Export the device list) and the compliance results (the device columns, then one column per check
/// that is on). A tool answers a count and a file reference; the CSV is written only when the file is fetched, from
/// <c>DeviceService.Search</c> and <c>ComplianceService</c> as the caller of then (rule 1). At most
/// <see cref="Sdk.Devices.DeviceLimits.MaxDevices"/> rows.
/// </summary>
internal static class ExportTools
{
    public static readonly string WatchResultsPath = InProcessCalls.PathOf(Proto.ComplianceService.Descriptor, "WatchResults");

    /// <summary>Rows of one search page while a CSV is written.</summary>
    public const int PageRows = 500;

    public static IReadOnlyList<string> DeviceColumns { get; } =
    [
        "MAC address", "Status", "Address", "Tags", "Site", "Host name", "Model", "AXIS OS", "SoC", "Architecture", "Category", "Product type",
        "DHCP", "HTTPS", "Certificate expires", "IEEE 802.1X",
    ];

    public static IEnumerable<OadmTool> Create(ToolHandles handles, Func<PermissionCatalog> catalog)
    {
        yield return new OadmTool(
            "oadm_devices_export",
            "Export devices",
            "The devices of a filter as a CSV file (the columns of Export devices). Answers the count and a file reference; fetch it with oadm_files_download_link.",
            Object(null, [P("filter", DeviceTools.Filter())], "filter"),
            OutObject(null,
            [
                P("devices", Out("integer", "Devices in the file.")),
                P("file", ToolFiles.OfferSchema("The CSV file.")),
            ], "devices", "file"),
            [DeviceTools.SearchPath],
            call => DevicesAsync(call, handles))
        {
            Gate = caller => caller.User.Has(HostPermissions.DevicesExport) ? null : catalog().DeniedMessage(HostPermissions.DevicesExport),
        };

        yield return new OadmTool(
            "oadm_compliance_export",
            "Export compliance",
            "The compliance results of a filter's devices as a CSV file: one row per device, one column per check that is on. Answers the count and a file reference.",
            Object(null, [P("filter", DeviceTools.Filter())], "filter"),
            OutObject(null,
            [
                P("devices", Out("integer", "Devices in the file.")),
                P("file", ToolFiles.OfferSchema("The CSV file.")),
            ], "devices", "file"),
            [DeviceTools.SearchPath, ComplianceTools.ListChecksPath, WatchResultsPath],
            call => ComplianceAsync(call, handles));
    }

    private static async Task<JsonObject> DevicesAsync(ToolCall call, ToolHandles handles)
    {
        var filter = DeviceTools.FilterOf(call.Element("filter"));
        var total = (await DeviceTools.SearchAsync(call, new Proto.SearchDevicesRequest { Filter = filter, Limit = 0 }).ConfigureAwait(false)).Total;
        var user = call.Caller.User;
        var calls = call.Calls;
        var name = "oadm-devices-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        var file = new PlannedDownload(name, "text/csv", async ct =>
        {
            var sb = new StringBuilder(256);
            CsvWriter.AppendRecord(sb, DeviceColumns);
            await foreach (var d in DevicesOfAsync(calls, user, filter, ct).ConfigureAwait(false))
            {
                CsvWriter.AppendRecord(sb,
                [
                    d.Serial, DeviceTools.StatusOf(d.Status), d.Address, string.Join("; ", d.Tags), d.SiteName, d.HostName, d.Model, d.FirmwareVersion, d.Soc,
                    d.Architecture, d.Category.ToString(), d.ProductType, YesNo(d.HasDhcpEnabled, d.DhcpEnabled), YesNo(d.HasHttpsEnabled, d.HttpsEnabled),
                    d.CertNotAfter?.ToDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), YesNo(d.HasDot1XEnabled, d.Dot1XEnabled),
                ]);
            }

            return new MemoryStream(CsvWriter.ToBytes(sb.ToString()));
        });
        return new JsonObject { ["devices"] = total, ["file"] = ToolFiles.Offer(call, handles, file) };
    }

    private static async Task<JsonObject> ComplianceAsync(ToolCall call, ToolHandles handles)
    {
        var filter = DeviceTools.FilterOf(call.Element("filter"));
        var total = (await DeviceTools.SearchAsync(call, new Proto.SearchDevicesRequest { Filter = filter, Limit = 0 }).ConfigureAwait(false)).Total;
        var user = call.Caller.User;
        var calls = call.Calls;
        var name = "oadm-compliance-" + DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".csv";
        var file = new PlannedDownload(name, "text/csv", async ct =>
        {
            var checks = (await calls.CallAsync<ComplianceGrpcService, Proto.Empty, Proto.ComplianceCheckList>(user, ComplianceTools.ListChecksPath, new Proto.Empty(),
                (s, r, c) => s.ListChecks(r, c), ct).ConfigureAwait(false)).Checks.Where(c => c.Enabled).ToList();
            var results = await ResultsAsync(calls, user, ct).ConfigureAwait(false);
            var sb = new StringBuilder(256);
            CsvWriter.AppendRecord(sb, ["Address", "Model", "MAC address", "Tags", .. checks.Select(c => c.Name)]);
            await foreach (var d in DevicesOfAsync(calls, user, filter, ct).ConfigureAwait(false))
            {
                CsvWriter.AppendRecord(sb, [d.Address, d.Model, d.Serial, string.Join("; ", d.Tags),
                    .. checks.Select(c => results.TryGetValue((c.Id, d.Id), out var r) ? ResultText(r) : string.Empty)]);
            }

            return new MemoryStream(CsvWriter.ToBytes(sb.ToString()));
        });
        return new JsonObject { ["devices"] = total, ["file"] = ToolFiles.Offer(call, handles, file) };
    }

    /// <summary>"Failed: Object Analytics is stopped", "Passed", "Not checked" (the texts of the Compliance page's export).</summary>
    internal static string ResultText(Proto.ComplianceResultEntry result) => result.Outcome switch
    {
        Proto.ComplianceOutcome.CompliancePassed => "Passed",
        Proto.ComplianceOutcome.ComplianceFailed => Join("Failed", result.Detail),
        Proto.ComplianceOutcome.ComplianceNotApplicable => Join("Not applicable", result.Detail),
        Proto.ComplianceOutcome.ComplianceCouldNotCheck => Join("Could not check", result.Detail),
        _ => Join("Not checked", result.Detail),
    };

    private static string Join(string outcome, string detail) => string.IsNullOrWhiteSpace(detail) ? outcome : outcome + ": " + detail;

    private static string? YesNo(bool known, bool value) => known ? (value ? "On" : "Off") : null;

    /// <summary>Every device of the filter in the caller's view, page by page in address order.</summary>
    private static async IAsyncEnumerable<Proto.Device> DevicesOfAsync(InProcessCalls calls, AuthenticatedCaller user, Proto.DeviceFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string afterKey = string.Empty, afterId = string.Empty;
        var rows = 0;
        while (rows < Sdk.Devices.DeviceLimits.MaxDevices)
        {
            var page = await calls.CallAsync<DeviceGrpcService, Proto.SearchDevicesRequest, Proto.SearchDevicesReply>(user, DeviceTools.SearchPath,
                new Proto.SearchDevicesRequest { Filter = filter, Sort = "address", AfterKey = afterKey, AfterId = afterId, Limit = PageRows },
                (s, r, c) => s.Search(r, c), ct).ConfigureAwait(false);
            for (var i = 0; i < page.Devices.Count; i++)
            {
                rows++;
                yield return page.Devices[i];
            }

            if (page.Devices.Count < PageRows || page.SortKeys.Count == 0)
            {
                yield break;
            }

            afterKey = page.SortKeys[^1];
            afterId = page.Devices[^1].Id;
        }
    }

    /// <summary>The compliance results the caller sees now (the snapshot of <c>WatchResults</c>), by check and device.</summary>
    private static async Task<Dictionary<(string Check, string Device), Proto.ComplianceResultEntry>> ResultsAsync(InProcessCalls calls, AuthenticatedCaller user, CancellationToken ct)
    {
        var results = new Dictionary<(string, string), Proto.ComplianceResultEntry>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = new SnapshotWriter(results, stop);
        try
        {
            await calls.CallAsync<ComplianceGrpcService, Proto.Empty, Proto.Empty>(user, WatchResultsPath, new Proto.Empty(), async (s, r, c) =>
            {
                await s.WatchResults(r, writer, c).ConfigureAwait(false);
                return new Proto.Empty();
            }, stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (writer.Ended && (ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled }))
        {
            // the snapshot is in: the watch was ended on purpose
        }

        return results;
    }

    /// <summary>Collects the snapshot of a result watch and ends the watch at its end.</summary>
    private sealed class SnapshotWriter(Dictionary<(string, string), Proto.ComplianceResultEntry> results, CancellationTokenSource stop) : IServerStreamWriter<Proto.ComplianceResultBatch>
    {
        public bool Ended { get; private set; }

        public WriteOptions? WriteOptions { get; set; }

        public async Task WriteAsync(Proto.ComplianceResultBatch message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (Ended)
            {
                return;
            }

            foreach (var entry in message.Results.Where(e => !e.Removed))
            {
                results[(entry.CheckId, entry.DeviceId)] = entry;
            }

            if (message.SnapshotEnd)
            {
                Ended = true;
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }
    }

}
