using System.Collections.Concurrent;

namespace Oadm.Sdk.Plugins;

/// <summary>
/// Serializes private journal updates across plugin activation generations. The SDK is shared by the host,
/// so unloading a plugin does not create another lock or change the process identity while its tasks finish.
/// This is process-local coordination; a data directory must belong to one OADM server process.
/// </summary>
public static class PluginDataLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Changes on server restart, but remains stable across plugin activation generations.</summary>
    public static Guid ProcessInstanceId { get; } = Guid.NewGuid();

    /// <summary>Holds the directory's lock until the returned lease is disposed.</summary>
    public static async Task<IDisposable> AcquireAsync(string directory, CancellationToken ct)
    {
        var gate = Gates.GetOrAdd(Path.GetFullPath(directory), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
