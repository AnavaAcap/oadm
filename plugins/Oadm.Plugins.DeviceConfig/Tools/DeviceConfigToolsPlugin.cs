using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig.Tools;

/// <summary>
/// The tools of Device configuration (<see cref="ConfigTools"/>): a core plugin without a page in the always-on package, so the
/// tools follow the package's on and off and Configure devices' permissions. It keeps no state and starts nothing; every call
/// reads only devices of the caller's view.
/// </summary>
public sealed class DeviceConfigToolsPlugin : ICorePlugin
{
    public string Id => DeviceConfigIds.PackageId;

    public string DisplayName => "Device configuration";

    public string? IconKey => "sliders";

    public bool HasPage => false;

    public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

    /// <summary>"config": <c>oadm_config_parameters</c>, <c>oadm_config_action_rules</c>, <c>oadm_config_stream_profiles</c>.</summary>
    public string? ToolArea => "config";

    public IReadOnlyList<PluginTool> Tools => ConfigTools.All;

    /// <summary>The clock of the read budget (tests).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
        throw new InvalidOperationException("Device configuration needs the user's call.");

    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
        await ConfigTools.InvokeAsync(caller, method, payloadJson, Time, ct).ConfigureAwait(false)
        ?? throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
}
