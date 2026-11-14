using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ModelContextProtocol.Client;

using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Certificates;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The real server with its MCP server on an in-memory test server (no network): a fake PKI offers a self-signed certificate
/// for the policy MCP server, the administrator turns MCP on through McpService, users get access keys through
/// AccessKeyService. <see cref="PostAsync"/> sends one JSON-RPC request with the headers of revision 2026-07-28;
/// <see cref="ConnectAsync"/> connects the official C# client.
/// </summary>
internal sealed class McpTestServer : IAsyncDisposable
{
    private McpTestServer(TestServerHost host, FakeServerCertificates certificates)
    {
        Host = host;
        Certificates = certificates;
    }

    public TestServerHost Host { get; }

    public FakeServerCertificates Certificates { get; }

    /// <summary>The last answer as received (status, content type, encoding, body), for messages of failed asserts.</summary>
    public string? LastRaw { get; private set; }

    public McpListener Listener => Host.Get<McpListener>();

    public static async Task<McpTestServer> StartAsync(FakeAxisNetwork? network = null, bool turnOn = true, bool withPki = true, Action<IServiceCollection>? configureServices = null,
        Oadm.Sdk.Network.IFirewallRules? firewall = null)
    {
        var host = await TestServerHost.StartAsync(network, configureServices: services =>
        {
            services.RemoveAll<McpListener>();
            services.AddSingleton(sp => new McpListener(sp, sp.GetRequiredService<ServerSettingsStore>(), sp.GetRequiredService<PluginServiceRegistry>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<McpListener>>(), firewall: firewall)
            {
                ConfigureHost = b => b.WebHost.UseTestServer(),
            });
            configureServices?.Invoke(services);
        });
        var certificates = new FakeServerCertificates();
        if (withPki)
        {
            host.Get<PluginServiceRegistry>().For("oadm.pki").Offer<IServerCertificates>(certificates);
            await host.Get<McpListener>().ApplyAsync(CancellationToken.None);
        }

        var server = new McpTestServer(host, certificates);
        if (turnOn)
        {
            await server.SetAsync(enabled: true);
        }

        return server;
    }

    public Proto.McpService.McpServiceClient McpService(CallInvoker? invoker = null) => new(invoker ?? Host.Invoker);

    public async Task<Proto.McpState> SetAsync(bool enabled, int port = McpSettings.DefaultPort, string listenOn = "") =>
        await McpService().SetAsync(new Proto.McpSettings { Enabled = enabled, Port = port, ListenOn = listenOn });

    /// <summary>A new access key of <paramref name="invoker"/>'s user (the administrator by default).</summary>
    public async Task<string> CreateKeyAsync(CallInvoker? invoker = null, string name = "agent", bool readOnly = false, int days = 90)
    {
        var reply = await new Proto.AccessKeyService.AccessKeyServiceClient(invoker ?? Host.Invoker)
            .CreateAsync(new Proto.CreateAccessKeyRequest { Name = name, Days = days, ReadOnly = readOnly });
        return reply.Secret;
    }

    /// <summary>An HTTP client of the running MCP server (an in-memory test server).</summary>
    public HttpClient Http()
    {
        var app = Listener.App ?? throw new InvalidOperationException("The MCP server does not listen.");
        var client = app.GetTestServer().CreateClient();
        client.BaseAddress = new Uri("https://localhost:5082/");
        return client;
    }

    /// <summary>The JSON-RPC body of <paramref name="method"/> with the per-request metadata of revision 2026-07-28.</summary>
    public static JsonObject Request(string method, JsonObject? parameters = null, int id = 1)
    {
        var p = parameters ?? [];
        p["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = McpEndpoint.ProtocolVersion,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "oadm-tests", ["version"] = "1.0.0" },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p };
    }

    /// <summary>Sends one request; <paramref name="headers"/> may override or remove (null) the standard ones.</summary>
    public async Task<McpReply> PostAsync(string? key, JsonObject body, IReadOnlyDictionary<string, string?>? headers = null, HttpClient? http = null)
    {
        var client = http ?? Http();
        using var message = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var standard = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MCP-Protocol-Version"] = McpEndpoint.ProtocolVersion,
            ["Mcp-Method"] = body["method"]?.GetValue<string>(),
            ["Mcp-Name"] = body["params"]?["name"]?.GetValue<string>(),
        };
        if (key is not null)
        {
            standard["Authorization"] = "Bearer " + key;
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string?>())
        {
            standard[name] = value;
        }

        foreach (var (name, value) in standard)
        {
            if (value is not null)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await client.SendAsync(message);
        var text = await response.Content.ReadAsStringAsync();
        LastRaw = $"{(int)response.StatusCode} {response.Content.Headers.ContentType} {string.Join(",", response.Content.Headers.ContentEncoding)} {text}";
        return new McpReply((int)response.StatusCode, response.Headers.RetryAfter?.ToString(), response.Content.Headers.ContentEncoding.ToList(), Json(text, response.Content.Headers.ContentType?.MediaType));
    }

    /// <summary>Calls a tool and returns the JSON-RPC result (the CallToolResult).</summary>
    public async Task<JsonObject> CallToolAsync(string key, string tool, JsonObject? arguments = null)
    {
        var reply = await PostAsync(key, Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? [] }));
        Assert.Equal(200, reply.Status);
        return reply.Body?["result"] as JsonObject ?? throw new InvalidOperationException("No result: " + reply.Body);
    }

    /// <summary>The structured content of a tool call that worked (and checks it equals the text content).</summary>
    public async Task<JsonObject> ToolAsync(string key, string tool, JsonObject? arguments = null)
    {
        var result = await CallToolAsync(key, tool, arguments);
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        var text = result["content"]![0]!["text"]!.GetValue<string>();
        Assert.True(JsonNode.DeepEquals(structured, JsonNode.Parse(text)), "The text content is the same JSON as the structured content.");
        return structured;
    }

    /// <summary>The names of the tools the key lists.</summary>
    public async Task<IReadOnlyList<string>> ToolNamesAsync(string key)
    {
        var reply = await PostAsync(key, Request("tools/list"));
        Assert.Equal(200, reply.Status);
        return [.. reply.Body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
    }

    /// <summary>The official C# client, connected with <paramref name="key"/> (revision 2026-07-28).</summary>
    public async Task<McpClient> ConnectAsync(string key)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("https://localhost:5082/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key },
        }, Http(), ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, new McpClientOptions
        {
            ProtocolVersion = McpEndpoint.ProtocolVersion,
            DiscoverProbeTimeout = TimeSpan.FromSeconds(60), // a busy test machine: never a fallback to initialize
            InitializationTimeout = TimeSpan.FromSeconds(60),
        });
    }

    private static JsonObject? Json(string text, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (mediaType == "text/event-stream")
        {
            // The last data line is the response.
            text = text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l["data:".Length..].Trim()).LastOrDefault() ?? string.Empty;
        }

        return JsonNode.Parse(text) as JsonObject;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        Certificates.Dispose();
    }
}

/// <summary>An answer of the MCP server: the HTTP status and the JSON-RPC body.</summary>
internal sealed record McpReply(int Status, string? RetryAfter, IReadOnlyList<string> ContentEncoding, JsonObject? Body)
{
    public string? ErrorMessage => Body?["error"]?["message"]?.GetValue<string>();

    public int? ErrorCode => Body?["error"]?["code"]?.GetValue<int>();
}

/// <summary>PKI's certificate of the MCP server: a self-signed CA and a server certificate it issued (or a problem).</summary>
internal sealed class FakeServerCertificates : IServerCertificates, IDisposable
{
    private readonly X509Certificate2 _ca;
    private readonly X509Certificate2 _leaf;

    public FakeServerCertificates()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=MCP server", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var signed = request.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [1, 2, 3, 4]);
        using var withKey = signed.CopyWithPrivateKey(key);
        _leaf = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Set: the policy has no usable certificate.</summary>
    public string? Problem { get; set; }

    public X509Certificate2 Ca => _ca;

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public Task<HostServerCertificate> LoadAsync(string server, CancellationToken ct) =>
        Task.FromResult(Problem is { } problem
            ? new HostServerCertificate([], problem)
            : new HostServerCertificate([X509CertificateLoader.LoadPkcs12(_leaf.Export(X509ContentType.Pfx), null), X509CertificateLoader.LoadCertificate(_ca.RawData)], null));

    public void Dispose()
    {
        _ca.Dispose();
        _leaf.Dispose();
    }
}
