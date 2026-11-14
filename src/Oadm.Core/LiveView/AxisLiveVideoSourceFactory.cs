using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView;

/// <summary>Opens live video streams of managed devices. Tests replace it with fakes.</summary>
public interface ILiveVideoSourceFactory
{
    /// <summary>Codecs and resolutions the device offers.</summary>
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct);

    /// <summary>Opens the stream; throws (usually <see cref="LiveViewException"/>) when the camera refuses.</summary>
    Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct);

    /// <summary>
    /// One JPEG of the source <paramref name="camera"/> at <paramref name="size"/> now (the camera's single picture request over
    /// the device's route). <paramref name="compression"/> is the camera's JPEG compression (1 to 100, 0: its default).
    /// </summary>
    /// <exception cref="LiveViewException">The camera refused or sent no picture.</exception>
    Task<byte[]> GetSnapshotAsync(Guid deviceId, int camera, ImageSize size, int compression, CancellationToken ct) =>
        throw new NotSupportedException("This source cannot take a snapshot.");
}

/// <summary>
/// Real factory: capabilities via param.cgi (Properties.Image) over the device's authenticated
/// VAPIX client, video via RTSP with the stored credentials on the device's RTSP port (554, or the port set with "OADM >
/// RTSP port" for a camera behind a port forward, <see cref="Sdk.Devices.IDeviceEventStreams.GetRtspPortAsync"/>).
/// Credentials never leave the server.
/// </summary>
public sealed class AxisLiveVideoSourceFactory(
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    ILoggerFactory? loggerFactory = null,
    Sdk.Devices.IDeviceEventStreams? ports = null,
    DeviceRouter? router = null,
    BasicOverHttpSetting? basicOverHttp = null) : ILiveVideoSourceFactory
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public async Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct)
    {
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        var image = await client.GetImageCapabilitiesAsync(ct).ConfigureAwait(false);
        return LiveViewCapabilities.FromImageCapabilities(image);
    }

    /// <summary>The longest wait for a picture.</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(15);

    /// <summary>The largest picture taken (the camera's own limit is far below).</summary>
    public const int MaxSnapshotBytes = 4 * 1024 * 1024;

    /// <summary>The request of one picture: <c>/axis-cgi/jpg/image.cgi?resolution=WxH&amp;camera=N[&amp;compression=C]</c>.</summary>
    public static Uri SnapshotUri(int camera, ImageSize size, int compression) =>
        new(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"/axis-cgi/jpg/image.cgi?resolution={size.Width}x{size.Height}&camera={Math.Max(1, camera)}{(compression > 0 ? $"&compression={compression}" : string.Empty)}"), UriKind.Relative);

    public async Task<byte[]> GetSnapshotAsync(Guid deviceId, int camera, ImageSize size, int compression, CancellationToken ct)
    {
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Get, SnapshotUri(camera, size, compression));
        message.Options.Set(Sdk.Vapix.VapixRequestOptions.Timeout, SnapshotTimeout);
        using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
        var jpeg = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : [];
        if (jpeg.Length < 3 || jpeg.Length > MaxSnapshotBytes || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new LiveViewException("The camera sent no picture.");
        }

        return jpeg;
    }

    public async Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var device = await devices.GetAsync(key.DeviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {key.DeviceId} not found.");
        var stored = await credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        var port = ports is null ? null : await ports.GetRtspPortAsync(device.Id, ct).ConfigureAwait(false);
        return await RtspVideoSource.OpenAsync(
            new RtspSourceOptions
            {
                Address = device.Address,
                Port = port ?? RtspClient.DefaultPort,
                Credentials = stored is null ? null : new NetworkCredential(stored.UserName, stored.Password),
                AllowBasic = basicOverHttp?.Current ?? false,
                BasicAllowed = basicOverHttp is null ? null : () => basicOverHttp.Current,
                Codec = key.Codec,
                Size = key.Size,
                Fps = key.Fps,
                Camera = key.Camera,
                Route = DeviceConnections.RouteOf(device),
                Router = router,
            },
            ct,
            _loggerFactory.CreateLogger<RtspVideoSource>()).ConfigureAwait(false);
    }
}
