using Oadm.Plugins.Shared;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Pipelines;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision;

/// <summary>What the page tools (<see cref="Api.VisionPageTools"/>) need of the running plugin: the page's checks without a write.</summary>
public sealed partial class VisionPlugin
{
    /// <summary>The context, store, runtime and models while the plugin runs; refuses while it is stopped.</summary>
    internal (ICorePluginContext Context, PipelineStore Store, PipelineRuntime Runtime, IModelLibrary Models) Running =>
        _ctx is { } ctx && _store is { } store && _runtime is { } runtime && _models is { } models
            ? (ctx, store, runtime, models)
            : throw new InvalidOperationException(PipelineRules.NotRunning);

    /// <summary>
    /// Every check of the page's Save (<see cref="VisionMethods.SavePipeline"/>) without storing: the size, the devices of the
    /// Device event nodes, the fields, the graph (<see cref="CheckWithEvents"/>, one error per node), the camera and the
    /// limits. A refusal is <see cref="PipelineFieldException"/> or the page's own exception.
    /// </summary>
    internal async Task<(Pipeline Pipeline, Pipeline? Before)> CheckSaveAsync(ICallContext caller, Pipeline incoming, CancellationToken ct)
    {
        var (ctx, store, runtime, _) = Running;
        if (PipelineRules.Oversized(incoming) is { } oversized)
        {
            throw new PipelineFieldException(new Dictionary<string, string>(StringComparer.Ordinal), [oversized]);
        }

        var existing = string.IsNullOrEmpty(incoming.Id) ? null : store.FindVisible(caller, incoming.Id);
        var pipelineOwner = existing is null ? OwnerScope.Of(caller.DeviceScopeKey) : OwnerScope.Of(existing.OwnerScopeKey);
        await RequireEventDevicesAsync(ctx, caller, pipelineOwner, incoming, ct).ConfigureAwait(false);
        return await store.CheckSaveAsync(ctx, caller, incoming, runtime.MaxWatching, p => CheckWithEvents(runtime, pipelineOwner, p, requireDevice: true), ctx.System.Devices.FindAsync, ct).ConfigureAwait(false);
    }

    /// <summary>The page's Copy check of the Device event nodes: their devices are the caller's (the copies' owner's).</summary>
    internal Task RequireCopyEventDevicesAsync(ICallContext caller, Pipeline original, CancellationToken ct) =>
        RequireEventDevicesAsync(Running.Context, caller, OwnerScope.Of(caller.DeviceScopeKey), original, ct);
}
