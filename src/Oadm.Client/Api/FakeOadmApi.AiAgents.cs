using System.Security.Cryptography;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake McpService and AccessKeyService so <c>--fake</c> shows the Settings card AI agents and the access keys: off at the
/// start with a certificate from the PKI policy, keys of the fake user and of a second user, the key itself shown once.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly List<AccessKey> _accessKeys =
    [
        new AccessKey
        {
            Id = "6b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e", UserName = "tech2", Name = "Laptop", ReadOnly = true,
            Created = Timestamp.FromDateTime(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc)),
            Expires = Timestamp.FromDateTime(new DateTime(2026, 12, 31, 8, 0, 0, DateTimeKind.Utc)),
            LastUsed = Timestamp.FromDateTime(new DateTime(2026, 11, 13, 16, 20, 0, DateTimeKind.Utc)),
        },
    ];

    private McpSettings _mcp = new() { Enabled = false, Port = 5082, ListenOn = string.Empty };

    public Task<McpState> GetMcpAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(McpStateNow());
        }
    }

    public Task<McpState> SetMcpAsync(McpSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            ThrowIfOffline();
            if (settings.Port is < 1025 or > 65535)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, Oadm.Contracts.Mcp.McpTexts.PortRange));
            }

            _mcp = settings.Clone();
            return Task.FromResult(McpStateNow());
        }
    }

    public Task<McpCaCertificate> GetMcpCaCertificateAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=OADM Root CA FAKE", key, HashAlgorithmName.SHA256);
        using var ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return Task.FromResult(new McpCaCertificate { FileName = "oadm-ca.pem", Pem = new string(PemEncoding.Write("CERTIFICATE", ca.RawData)) + "\n" });
    }

    public Task<IReadOnlyList<AccessKey>> ListAccessKeysAsync(bool everyUser, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(KeysOf(everyUser));
        }
    }

    public Task<CreatedAccessKey> CreateAccessKeyAsync(string name, int days, bool readsOnly, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var clean = (name ?? string.Empty).Trim();
            if (_accessKeys.Any(k => k.UserName == "admin" && string.Equals(k.Name, clean, StringComparison.OrdinalIgnoreCase)))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "You already have an access key with this name."));
            }

            var now = DateTime.UtcNow;
            var key = new AccessKey
            {
                Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                UserName = "admin",
                Name = clean,
                ReadOnly = readsOnly,
                Created = Timestamp.FromDateTime(now),
                Expires = Timestamp.FromDateTime(now.AddDays(days)),
            };
            _accessKeys.Add(key);
            var secret = "oadm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Task.FromResult(new CreatedAccessKey { Key = key, Secret = secret });
        }
    }

    public Task<IReadOnlyList<AccessKey>> DeleteAccessKeyAsync(string id, bool anyUser, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (_accessKeys.RemoveAll(k => k.Id == id && (anyUser || k.UserName == "admin")) == 0)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "The access key no longer exists."));
            }

            return Task.FromResult(KeysOf(anyUser));
        }
    }

    private IReadOnlyList<AccessKey> KeysOf(bool everyUser) =>
        [.. _accessKeys.Where(k => everyUser || k.UserName == "admin").OrderBy(k => k.UserName, StringComparer.OrdinalIgnoreCase).ThenByDescending(k => k.Created.ToDateTime())];

    private McpState McpStateNow()
    {
        var state = new McpState
        {
            Settings = _mcp.Clone(),
            Running = _mcp.Enabled,
            Status = _mcp.Enabled ? Oadm.Contracts.Mcp.McpTexts.Running(_mcp.Port) : Oadm.Contracts.Mcp.McpTexts.Off,
            PkiAvailable = true,
            Certificate = "MCP server (OADM Root CA FAKE), until 2036-11-14",
            HasCaCertificate = true,
        };
        state.CertificateNames.AddRange(["oadm-server.local", "10.0.0.17"]);
        state.Interfaces.Add(new McpInterface { Id = "all", Label = "All interfaces", Name = "All interfaces" });
        state.Interfaces.Add(new McpInterface { Id = "eth0", Label = "Ethernet - 10.0.0.17", Name = "Ethernet", Addresses = { "10.0.0.17" } });
        return state;
    }
}
