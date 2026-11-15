using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;

using ModelContextProtocol.Protocol;

using Oadm.Server.Tools;

namespace Oadm.Server.Mcp;

/// <summary>Where and with which certificate the MCP server listens.</summary>
/// <param name="Port">The TCP port.</param>
/// <param name="Addresses">The addresses of the chosen interface; null for every interface.</param>
/// <param name="Certificate">The certificate with its key (PKI's policy MCP server).</param>
/// <param name="Issuers">Its issuers, sent with it so agents can build the chain to the CA they trust.</param>
public sealed record McpListenOptions(int Port, IReadOnlyList<IPAddress>? Addresses, X509Certificate2 Certificate, IReadOnlyList<X509Certificate2> Issuers);

/// <summary>
/// The MCP server's own web host (docs/spec/mcp.md "Transport and certificate"): Kestrel in the server process on its own port
/// with the PKI certificate (TLS 1.2 or later), one endpoint <c>POST /mcp</c>, the official C# SDK with Streamable HTTP,
/// stateless (no session id, no stream on GET) and pinned to the protocol revision <see cref="ProtocolVersion"/> (no
/// <c>initialize</c>; the SDK checks <c>MCP-Protocol-Version</c>, <c>Mcp-Method</c> and <c>Mcp-Name</c> against the body,
/// -32020). In front of it: a browser origin other than the server (a name of its certificate) is refused (403), bodies are at most
/// <see cref="MaxBodyBytes"/> (413), every request needs a valid access key (401) and each key at most
/// <see cref="McpAccess.CallsPerSecond"/> calls a second, <see cref="McpAccess.CallsAtOnce"/> at once and
/// <see cref="McpAccess.ChangesPerMinute"/> changes a minute (and its user's keys together the same, the whole port at most
/// <see cref="McpAccess.CallsInPort"/> at once; 429; the changes and the camera pictures, <see cref="LiveView.SnapshotAudit.PicturesPerMinute"/> a minute per
/// user, are counted from the tool name of the call itself); file transfers have their own places (<see cref="McpAccess.TransfersAtOnce"/> per
/// user, <see cref="McpAccess.TransfersInPort"/> on the port) and must keep up <see cref="TransferMinBytesPerSecond"/> and end within <see cref="McpFiles.TransferBudget"/>, and "Use AI agents" for the files and the skill too (403). Answers are not
/// compressed (no gzip: the SDK's event stream stays as it is; they are at most <see cref="ToolAnswers.MaxBytes"/>).
/// </summary>
public static class McpEndpoint
{
    public const string Path = "/mcp";

    /// <summary>The MCP revision the server speaks.</summary>
    public const string ProtocolVersion = "2026-07-28";

    /// <summary>The largest request body (files go through upload links, stage 2).</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    internal const string CallerKey = "oadm.mcp.caller";

    public const string BrowserRefused = "Requests from web pages are not accepted.";
    public const string TooLarge = "The request is larger than 1 MB.";
    public const string TooManyCalls = "Too many calls with this access key. Wait a second.";
    public const string TooManyAtOnce = "Too many calls at once. Wait for an answer.";
    public const string TooManyChanges = "Too many changes with this access key. Wait a minute.";
    public const string TooManyPictures = LiveView.SnapshotAudit.TooManyPictures;

    /// <summary>A file transfer must send or take at least this many bytes a second (after <see cref="TransferGrace"/>); a stalled one ends and frees its place.</summary>
    public const double TransferMinBytesPerSecond = 1024;

    public static readonly TimeSpan TransferGrace = TimeSpan.FromSeconds(10);

    /// <summary>Where upload links point: <c>PUT /files/&lt;token&gt;</c> with the same key (docs/spec/mcp.md "Files").</summary>
    public const string FilesPath = "/files/";
    public const string NoProtocolVersion = "The MCP-Protocol-Version header is missing.";
    public const string TooManyRefused = "Too many refused access keys from your address. Try again in a few minutes.";

    /// <summary>The JSON-RPC error of headers that are missing or do not match the body (MCP "HeaderMismatch").</summary>
    public const int HeaderMismatch = -32020;

    /// <summary>
    /// The web host of the MCP server. <paramref name="configure"/> replaces Kestrel in tests (an in-memory test server: no
    /// network in unit tests). <paramref name="serverNames"/> are the names and addresses of the certificate: a browser origin
    /// must be one of them (null: not checked).
    /// </summary>
    internal static WebApplication Build(IServiceProvider server, McpListenOptions? listen, Action<WebApplicationBuilder>? configure = null, IReadOnlyCollection<string>? serverNames = null, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var tools = server.GetRequiredService<McpToolAdapter>();
        var snapshots = server.GetRequiredService<LiveView.SnapshotAudit>();
        var access = server.GetRequiredService<McpAccess>();
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Services.AddSingleton<ILoggerFactory>(new QuietWebLoggerFactory(server.GetRequiredService<ILoggerFactory>())); // no request path (an upload token) in the server log
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.Services.AddRouting();
        builder.Services.AddHttpContextAccessor();
        builder.Services
            .AddMcpServer(o =>
            {
                o.ProtocolVersion = ProtocolVersion;
                o.ServerInfo = new Implementation { Name = "OADM", Version = Mapping.Mappers.ServerVersion };
                o.Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false },
                    Resources = new ResourcesCapability { ListChanged = false, Subscribe = false },
                    Extensions = new Dictionary<string, object> { [McpSkill.Extension] = new JsonObject() },
                };
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListResourcesHandler((request, ct) => ValueTask.FromResult(Allowed(access, request.Services) ? McpSkill.List() : new ListResourcesResult()))
            .WithReadResourceHandler((request, ct) =>
            {
                Allowed(access, request.Services, refuse: true);
                return ValueTask.FromResult(McpSkill.Read(request.Params?.Uri));
            })
            .WithListToolsHandler((request, ct) => ValueTask.FromResult(tools.List(CallerOf(request.Services))))
            .WithCallToolHandler(async (request, ct) =>
            {
                // The limits of changes and pictures count here, from the call's own tool name (never from a header): a call over its limit is answered 429.
                CountCall(access, snapshots, tools.Tools, request.Services, request.Params?.Name);
                var arguments = request.Params?.Arguments is { } given ? JsonSerializer.SerializeToElement(given) : (JsonElement?)null;
                return await tools.CallAsync(CallerOf(request.Services), request.Params?.Name ?? string.Empty, arguments, ct).ConfigureAwait(false);
            });

        if (listen is not null)
        {
            builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = MaxBodyBytes;
                k.Limits.MinRequestBodyDataRate = new MinDataRate(TransferMinBytesPerSecond, TransferGrace); // also what the files routes ask for per request
                void Https(ListenOptions l)
                {
                    l.Protocols = HttpProtocols.Http1AndHttp2;
                    l.UseHttps(new HttpsConnectionAdapterOptions
                    {
                        ServerCertificate = listen.Certificate,
                        ServerCertificateChain = [.. listen.Issuers],
                        SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateMode = ClientCertificateMode.NoCertificate,
                    });
                }

                if (listen.Addresses is null)
                {
                    k.ListenAnyIP(listen.Port, Https);
                }
                else
                {
                    foreach (var address in listen.Addresses)
                    {
                        k.Listen(address, listen.Port, Https);
                    }
                }
            });
        }

        configure?.Invoke(builder);
        var app = builder.Build();
        var files = server.GetRequiredService<McpFiles>();
        var registry = server.GetRequiredService<ToolRegistry>();
        var filesUrl = FilesUrlOf(serverNames, port);
        app.Use((http, next) => GuardAsync(http, next, access, serverNames, files, registry, filesUrl));
        app.MapMcp(Path);
        return app;
    }

    /// <summary>
    /// Where upload links point: the server's own name (the first DNS name of its certificate, else its first address, as Connect
    /// an agent shows it) and the configured port, never the request's <c>Host</c> header; null while either is not known (no
    /// link is made).
    /// </summary>
    internal static string? FilesUrlOf(IReadOnlyCollection<string>? names, int? port)
    {
        var all = (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        var chosen = all.FirstOrDefault(n => !IPAddress.TryParse(n, out _)) ?? all.FirstOrDefault();
        if (chosen is null || port is null)
        {
            return null;
        }

        var host = IPAddress.TryParse(chosen, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + chosen + "]" : chosen;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"https://{host}:{port}{FilesPath}");
    }

    /// <summary>The skill and its resources need "Use AI agents" like the tools do (the list is empty, a read is refused).</summary>
    private static bool Allowed(McpAccess access, IServiceProvider? services, bool refuse = false)
    {
        var caller = CallerOf(services);
        if (access.ChannelRefusal(caller) is not { } text)
        {
            return true;
        }

        return refuse ? throw new ModelContextProtocol.McpProtocolException(text, ModelContextProtocol.McpErrorCode.InvalidRequest) : false;
    }

    /// <summary>The tool caller the guard made from the access key (the only thing of the request the tools get).</summary>
    private static ToolCaller CallerOf(IServiceProvider? services) =>
        services?.GetService<IHttpContextAccessor>()?.HttpContext?.Items[CallerKey] as ToolCaller
        ?? throw new InvalidOperationException("An MCP request without its caller."); // the guard runs first: never reached

    /// <summary>The checks before the SDK sees a request.</summary>
    private static async Task GuardAsync(HttpContext http, RequestDelegate next, McpAccess access, IReadOnlyCollection<string>? serverNames, McpFiles files, ToolRegistry tools, string? filesUrl)
    {
        var path = http.Request.Path.Value ?? string.Empty;
        var upload = path.StartsWith(FilesPath, StringComparison.Ordinal) ? path[FilesPath.Length..] : null;
        if (!string.Equals(path, Path, StringComparison.Ordinal) && (upload is null || upload.Length == 0 || upload.Contains('/', StringComparison.Ordinal)))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!SameOrigin(http, serverNames))
        {
            await RefuseAsync(http, StatusCodes.Status403Forbidden, BrowserRefused).ConfigureAwait(false);
            return;
        }

        // /mcp takes POST only (no stream on GET, no session to DELETE); a link of /files/ takes PUT (an upload) or GET (a download).
        var download = upload is not null && HttpMethods.IsGet(http.Request.Method);
        var method = upload is null ? HttpMethods.Post : download ? HttpMethods.Get : HttpMethods.Put;
        if (!string.Equals(http.Request.Method, method, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.Headers.Allow = upload is null ? HttpMethods.Post : HttpMethods.Put + ", " + HttpMethods.Get;
            http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        if (upload is null && http.Request.ContentLength > MaxBodyBytes)
        {
            await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, TooLarge).ConfigureAwait(false);
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = upload is null ? MaxBodyBytes : null; // also a body without a length; an upload is bound by its link
        }

        var header = http.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var address = AddressOf(http);
        if (access.IsAddressLimited(address))
        {
            http.Response.Headers.RetryAfter = "300";
            await RefuseAsync(http, StatusCodes.Status429TooManyRequests, TooManyRefused).ConfigureAwait(false);
            return;
        }

        // Counted as soon as the key validates, before its role and device view are read (a flood costs no more than a refusal).
        var signIn = await access.SignInAsync(key, address, http.RequestAborted).ConfigureAwait(false);
        if (signIn.Throttled)
        {
            http.Response.Headers.RetryAfter = "1";
            await RefuseAsync(http, StatusCodes.Status429TooManyRequests, TooManyCalls).ConfigureAwait(false);
            return;
        }

        if (signIn.Caller is not { } caller)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await RefuseAsync(http, StatusCodes.Status401Unauthorized, Core.Auth.AccessKeyTexts.NotValid).ConfigureAwait(false);
            return;
        }

        // "Use AI agents" for the file routes (the tools and the skill check it themselves): a key whose role lost it neither sends nor fetches a file.
        if (upload is not null && access.ChannelRefusal(caller) is { } denied)
        {
            await RefuseAsync(http, StatusCodes.Status403Forbidden, denied).ConfigureAwait(false);
            return;
        }

        if (upload is not null)
        {
            using var uploading = access.TryStartTransfer(caller); // a pool of its own: a slow transfer never takes the places of /mcp calls
            if (uploading is null)
            {
                http.Response.Headers.RetryAfter = "1";
                await RefuseAsync(http, StatusCodes.Status429TooManyRequests, TooManyAtOnce).ConfigureAwait(false);
                return;
            }

            RequireTransferRate(http);
            if (download)
            {
                await files.SendAsync(http, caller, upload).ConfigureAwait(false);
            }
            else
            {
                await files.ReceiveAsync(http, caller, upload).ConfigureAwait(false);
            }

            return;
        }

        if (http.Request.Headers["MCP-Protocol-Version"].Count == 0)
        {
            // The SDK would serve it as an older revision; this server speaks only ProtocolVersion (Streamable HTTP "Server Validation").
            await RefuseAsync(http, StatusCodes.Status400BadRequest, NoProtocolVersion, HeaderMismatch).ConfigureAwait(false);
            return;
        }

        using var inFlight = access.TryStartCall(caller);
        if (inFlight is null)
        {
            http.Response.Headers.RetryAfter = "1";
            await RefuseAsync(http, StatusCodes.Status429TooManyRequests, TooManyAtOnce).ConfigureAwait(false);
            return;
        }

        // The tools get the caller and where this channel takes files (from the server's own name), nothing else of the request.
        http.Items[CallerKey] = caller with { Channel = caller.Channel with { FilesUrl = filesUrl } };
        await next(http).ConfigureAwait(false);
    }

    /// <summary>
    /// Counts the call of <paramref name="name"/> against the key's and the user's limit of changes; over it the answer is HTTP 429
    /// with Retry-After 60 (nothing was counted, nothing runs). A call the registry refuses (role, Read only, "Use AI agents") and an
    /// unknown tool count against no limit. The pictures are counted by <c>GetSnapshot</c> itself, for every channel; here only a user who
    /// has used them up is answered 429 before the call goes on.
    /// </summary>
    private static void CountCall(McpAccess access, LiveView.SnapshotAudit snapshots, ToolRegistry tools, IServiceProvider? services, string? name)
    {
        if (name is null || !tools.TryGet(name, out var tool))
        {
            return; // an unknown tool is the adapter's protocol error
        }

        var caller = CallerOf(services);
        if (tools.Refusal(tool, caller) is not null)
        {
            return; // the registry refuses it: it costs the user nothing
        }

        var refusal = tool.CountsAsChange && !access.TryChange(caller) ? TooManyChanges
            : tool.CountsAsPicture && snapshots.IsLimited(caller.User.UserId) ? TooManyPictures
            : null;
        if (refusal is null)
        {
            return;
        }

        if (services?.GetService<IHttpContextAccessor>()?.HttpContext is { } http && !http.Response.HasStarted)
        {
            http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            http.Response.Headers.RetryAfter = "60";
        }

        throw new ModelContextProtocol.McpProtocolException(refusal, ModelContextProtocol.McpErrorCode.InvalidRequest);
    }

    /// <summary>A file transfer that stalls ends (Kestrel's minimum data rate, <see cref="TransferMinBytesPerSecond"/> after <see cref="TransferGrace"/>).</summary>
    internal static void RequireTransferRate(HttpContext http)
    {
        var rate = new MinDataRate(TransferMinBytesPerSecond, TransferGrace);
        try
        {
            if (http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } body)
            {
                body.MinDataRate = rate;
            }

            if (http.Features.Get<IHttpMinResponseDataRateFeature>() is { } response)
            {
                response.MinDataRate = rate;
            }
        }
        catch (NotSupportedException)
        {
            // a server without the rule (the in-memory test server): nothing to set
        }
    }

    /// <summary>
    /// No Origin (an agent, not a browser), or the server itself: the scheme, host and port of the request, and a host that
    /// is one of <paramref name="serverNames"/> (the certificate's names and addresses). The Host header alone is not enough:
    /// with DNS rebinding the browser sends the page's own name as both Origin and Host.
    /// </summary>
    internal static bool SameOrigin(HttpContext http, IReadOnlyCollection<string>? serverNames = null)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, http.Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && (serverNames is null || serverNames.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase));
    }

    private static string AddressOf(HttpContext http)
    {
        var ip = http.Connection.RemoteIpAddress;
        return ip is null ? "local" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    /// <summary>An HTTP refusal with a JSON-RPC error without id as its body (Streamable HTTP "Security").</summary>
    private static Task RefuseAsync(HttpContext http, int status, string message, int code = -32600)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        return http.Response.WriteAsync(body.ToJsonString(), http.RequestAborted);
    }
}

/// <summary>
/// The server's loggers for the MCP web host with ASP.NET's own categories kept to Warning and above whatever the server's log
/// settings say: its Information lines carry the request path, and the path of an upload link holds its token (and
/// <c>oadm_logs_search</c> serves that log).
/// </summary>
internal sealed class QuietWebLoggerFactory(ILoggerFactory inner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName)
    {
        var logger = inner.CreateLogger(categoryName);
        return categoryName.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) ? new Quiet(logger) : logger;
    }

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
        // the server's own factory is not ours to dispose
    }

    private sealed class Quiet(ILogger logger) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null; // a scope would carry the path too

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                logger.Log(logLevel, eventId, state, exception, formatter);
            }
        }
    }
}
