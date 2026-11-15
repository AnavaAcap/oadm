using System.Text.Json;

using Oadm.Plugins.EdgeRecordingExport.Recordings;
using Oadm.Plugins.EdgeRecordingExport.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.EdgeRecordingExport;

/// <summary>
/// Core plugin "Edge Recording Export": finds the recordings on the SD card or network share of chosen cameras in a time
/// range and passes each one's export (MP4, else Matroska) on to the client, which saves them in a folder per camera.
/// Off by default (plugin.json). The page drives the work camera by camera and file by file; the server keeps no state
/// and stores no file. Read-only for the cameras; every camera comes from the user's call (device groups, SDK 0.4.0).
/// </summary>
public sealed class EdgeRecordingExportPlugin : ICorePlugin, ICorePluginDownloads
{
    private readonly EdgeRecordingOptions _options;
    private EdgeRecordingService? _service;

    public EdgeRecordingExportPlugin()
        : this(new EdgeRecordingOptions())
    {
    }

    public EdgeRecordingExportPlugin(EdgeRecordingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Id => EdgeRecordingInfo.PluginId;

    public string DisplayName => EdgeRecordingInfo.DisplayName;

    public string? IconKey => EdgeRecordingInfo.IconKey;

    public CorePluginGroup Group => CorePluginGroup.Maintenance;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    public IReadOnlyList<PermissionDefinition> Permissions { get; } =
    [
        new(PermissionKeys.Show, "Show the page", "See the page and search the recordings of the cameras.") { ViewerDefault = true },
        new(EdgeRecordingInfo.ExportKey, "Export recordings", "Download recordings from the cameras to this computer."),
    ];

    public string? RequiredPermission(string method) => method switch
    {
        EdgeRecordingMethods.Search => PermissionKeys.Show,
        _ when RecordingTools.Search.Calls(method) => PermissionKeys.Show,
        _ when RecordingTools.Export.Calls(method) => EdgeRecordingInfo.ExportKey,
        EdgeRecordingMethods.Export => EdgeRecordingInfo.ExportKey,
        _ => null,
    };

    /// <summary>The read-only tool <c>oadm_recordings_search</c> (docs/spec/mcp.md "Plugins"): the page's search, "show".</summary>
    public IReadOnlyList<PluginTool> Tools => RecordingTools.All;

    /// <summary>The tools are named <c>oadm_recordings_...</c>.</summary>
    public string? ToolArea => "recordings";

    /// <summary>Every downloaded recording is in the audit log (camera and time range).</summary>
    public bool IsAudited(string method) => method == EdgeRecordingMethods.Export;

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _service = new EdgeRecordingService(ctx, _options);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _service = null;
        return Task.CompletedTask;
    }

    /// <summary>Only through the user's call (<see cref="InvokeAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the Edge Recording Export plugin with the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The Edge Recording Export plugin is not running.");
        return method switch
        {
            EdgeRecordingMethods.Search => JsonSerializer.Serialize(
                await service.SearchAsync(caller, Read<SearchRequest>(payloadJson), ct).ConfigureAwait(false), EdgeRecordingInfo.Json),
            _ when method == RecordingTools.Search.Method => await SearchToolAsync(service, caller, payloadJson, ct).ConfigureAwait(false),
            _ when method == RecordingTools.Export.Method => await ExportToolAsync(caller, payloadJson, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown method '{method}'.", nameof(method)),
        };
    }

    /// <summary>The tool's search: the page's search of one camera (the caller's devices only), then one page of its recordings.</summary>
    private static async Task<string> SearchToolAsync(EdgeRecordingService service, ICallContext caller, string? payloadJson, CancellationToken ct)
    {
        var (request, errors) = RecordingTools.RequestOf(payloadJson);
        return request is null ? errors! : RecordingTools.Answer(await service.SearchAsync(caller, request, ct).ConfigureAwait(false), payloadJson);
    }

    /// <summary>
    /// The export tool: the file of one recording as the page's download (fetched later with the caller's rights, audited then).
    /// A camera outside the caller's view answers like an unknown one; nothing is read from the camera here.
    /// </summary>
    private static async Task<string> ExportToolAsync(ICallContext caller, string? payloadJson, CancellationToken ct)
    {
        var (request, errors) = RecordingTools.ExportRequestOf(payloadJson);
        if (request is null)
        {
            return errors!;
        }

        var device = await caller.Devices.FindAsync(request.DeviceId, ct).ConfigureAwait(false);
        if (device is null)
        {
            return caller.HasAllDevices ? PluginToolErrors.Answer([new("device_id", RecordingTools.EnterCamera)]) : throw new DevicesNotAvailableException();
        }

        if (!device.HasVideo)
        {
            return PluginToolErrors.Answer([new("device_id", EdgeRecordingTexts.NotVideo)]);
        }

        return RecordingTools.ExportAnswerOf(request, device.HostName ?? device.Address);
    }

    /// <summary>Only through the user's call (<see cref="OpenDownloadAsync(ICallContext, string, string?, CancellationToken)"/>).</summary>
    public Task<PluginDownload> OpenDownloadAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new NotSupportedException("Call the Edge Recording Export plugin with the user's call.");

    public Task<PluginDownload> OpenDownloadAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        var service = _service ?? throw new InvalidOperationException("The Edge Recording Export plugin is not running.");
        return method == EdgeRecordingMethods.Export
            ? service.OpenExportAsync(caller, Read<ExportRequest>(payloadJson), ct)
            : throw new ArgumentException($"Unknown download '{method}'.", nameof(method));
    }

    private static T Read<T>(string? json) => PluginJson.Parse<T>(json, EdgeRecordingInfo.Json);
}
