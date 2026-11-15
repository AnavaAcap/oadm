using System.Text.Json;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>Answer of the export query: the device's export, or why there is none.</summary>
public sealed record ExportReply
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>The device's <c>$export</c> answer, unchanged.</summary>
    public string? Text { get; init; }

    public string? Error { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static ExportReply Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new ExportReply { Error = "The server sent no answer." } : JsonSerializer.Deserialize<ExportReply>(json, Options) ?? new ExportReply();
}

/// <summary>
/// "Export configuration" (Maintenance): in the client a read-only dialog that runs no task; an AI agent's run is a read-only
/// task per device whose result is the export in its log (<see cref="ExecuteAsync"/>). The dialog reads every device through the query
/// <see cref="DeviceConfigIds.ExportQuery"/> (at most <see cref="DeviceConfigIds.ExportParallelism"/> at a time) and saves
/// the answers.
/// </summary>
public sealed class ExportConfigTask : ITaskPlugin, ITaskPluginQuery
{
    public string Id => DeviceConfigIds.Export;

    public string DisplayName => DeviceConfigIds.ExportName;

    public string? IconKey => "export";

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    public bool CanRun(IDeviceInfo device) => DeviceConfigSupport.CanRun(device);

    public string? NotSupportedReason(IDeviceInfo device) => DeviceConfigSupport.NotSupportedReason(device);

    /// <summary>The input for AI agents (SDK 0.13.0): none. The export of each device is its task's result.</summary>
    public Type? PayloadType => typeof(ExportConfigMcpPayload);

    public const string NoSettings = "The export takes no settings.";

    /// <summary>Nothing to choose: an empty object (or none).</summary>
    public IReadOnlyList<Oadm.Sdk.Tasks.PayloadError> ValidatePayload(string? payloadJson, ICallContext caller) => Check(payloadJson);

    private static IReadOnlyList<Oadm.Sdk.Tasks.PayloadError> Check(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.EnumerateObject().Any() ? [] : [new(string.Empty, NoSettings)];
        }
        catch (JsonException)
        {
            return [new(string.Empty, NoSettings)];
        }
    }

    public static class Steps
    {
        public const string CheckCompatibility = "Check compatibility";
        public const string Export = "Export configuration";
    }

    /// <summary>
    /// Read only (the dialog reads through <see cref="DeviceConfigIds.ExportQuery"/> instead): the device's export, unchanged,
    /// as the task's result in its log (<see cref="TaskFileResult"/>; TODO(download-link)).
    /// </summary>
    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (Check(payloadJson) is [var problem, ..])
        {
            throw new ArgumentException(problem.Message, nameof(payloadJson));
        }

        ctx.PlanSteps(Steps.CheckCompatibility, Steps.Export);
        using (var step = ctx.BeginStep(Steps.CheckCompatibility))
        {
            if (TaskSupportReasons.ForStatus(device) is { } refused)
            {
                throw new ConfigDeviceException(refused + ".");
            }

            var info = await ctx.Vapix.GetBasicDeviceInfoAsync(ct).ConfigureAwait(false);
            if (!FirmwareVersions.SupportsConfigFiles(info.Version))
            {
                throw new ConfigDeviceException($"Needs AXIS OS {DeviceConfigIds.MinFirmwareText} or later (this device has {info.Version}).");
            }

            step.Complete($"{ModelNames.Display(info.ProdNbr)}, AXIS OS {info.Version}");
        }

        using (var step = ctx.BeginStep(Steps.Export))
        {
            var text = await ConfigApi.ExportAsync(ctx.Vapix, ct).ConfigureAwait(false);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var name = ConfigFileNames.ForDevice(device.Address, device.Serial, device.Model);
            TaskFileResult.Log(ctx, name, bytes);
            step.Complete(TaskFileResult.Summary(name, bytes.Length));
        }
    }

    /// <summary>The user's query (SDK 0.4.0): reads only the queried device, which the host checked against the user's view.</summary>
    public Task<string?> QueryAsync(ITaskQueryContext ctx, ICallContext caller, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct) =>
        QueryAsync(ctx, device, method, payloadJson, ct);

    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, DeviceConfigIds.ExportQuery, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown query '{method}'.", nameof(method));
        }

        if (TaskSupportReasons.ForStatus(device) is { } refused)
        {
            return new ExportReply { Error = refused }.ToJson();
        }

        try
        {
            return new ExportReply { Text = await ConfigApi.ExportAsync(ctx.Vapix, ct).ConfigureAwait(false) }.ToJson();
        }
        catch (ConfigDeviceException ex)
        {
            return new ExportReply { Error = ex.Message }.ToJson();
        }
    }
}

/// <summary>Which devices export and import configuration files (cached data only).</summary>
public static class DeviceConfigSupport
{
    public static bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status == DeviceStatus.Ok && FirmwareVersions.SupportsConfigFiles(device.FirmwareVersion);
    }

    public static string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device, requireOk: true)
            ?? TaskSupportReasons.NeedsFirmware(DeviceConfigIds.MinFirmwareText, device);
    }
}
