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
        message.Options.Set(Sdk.Vapix.VapixRequestOptions.StreamResponse, true); // not buffered: the cap below stops the read
        using var response = await client.SendAsync(message, ct).ConfigureAwait(false);
        using var bodyTime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTime.CancelAfter(SnapshotTimeout); // the timeout above covers only the time until the headers
        var jpeg = response.IsSuccessStatusCode ? await ReadCappedAsync(response.Content, MaxSnapshotBytes, bodyTime.Token).ConfigureAwait(false) : [];
        if (jpeg.Length < 3 || jpeg.Length > MaxSnapshotBytes || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new LiveViewException("The camera sent no picture.");
        }

        return jpeg;
    }

    /// <summary>
    /// The body of <paramref name="content"/>, read at most <paramref name="limit"/> bytes: a longer answer stops the read at
    /// the limit (one byte more than the limit is returned, so the caller sees it is too large) instead of being read in full.
    /// </summary>
    internal static async Task<byte[]> ReadCappedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is > 0 and var length && length > limit)
        {
            return new byte[limit + 1]; // announced too large: nothing is read
        }

        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (buffer.Length <= limit)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit + 1 - buffer.Length)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
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
