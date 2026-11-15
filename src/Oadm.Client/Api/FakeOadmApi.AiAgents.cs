using System.Security.Cryptography;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake McpService and AccessKeyService so <c>--fake</c> shows the Settings card AI, section MCP, and its access keys: off at
/// the start with a certificate from the PKI policy, one key with the role Viewer, a new key shown once.
/// </summary>
public sealed partial class FakeOadmApi
{
    private readonly List<AccessKey> _accessKeys =
    [
        new AccessKey
        {
            Id = "6b1c2d3e-4f5a-6b7c-8d9e-0f1a2b3c4d5e", Name = "Health check", RoleId = ViewerRoleId, RoleName = "Viewer", AllDevices = true,
            CreatedBy = "admin",
            Created = Timestamp.FromDateTime(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc)),
            Expires = Timestamp.FromDateTime(new DateTime(2026, 12, 31, 8, 0, 0, DateTimeKind.Utc)),
            LastUsed = Timestamp.FromDateTime(new DateTime(2026, 11, 13, 16, 20, 0, DateTimeKind.Utc)),
        },
    ];

    public Task<IReadOnlyList<AccessKey>> ListAccessKeysAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(KeysNow());
        }
    }

    public Task<CreatedAccessKey> CreateAccessKeyAsync(CreateAccessKeyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            ThrowIfOffline();
            SeedGroupsLocked();
            var clean = request.Name.Trim();
            if (_accessKeys.Any(k => string.Equals(k.Name, clean, StringComparison.OrdinalIgnoreCase)))
            {
                throw KeyRefused("name", "An access key with this name exists.");
            }

            var role = _roles.FirstOrDefault(r => r.Id == request.RoleId) ?? throw KeyRefused("role", "The role does not exist (any more).");
            var admin = role.Kind == RoleKind.Administrator;
            var group = admin || request.AllDevices ? null
                : _groups.FirstOrDefault(g => g.Id == request.DeviceGroupId) ?? throw KeyRefused("devices", "The device group does not exist (any more).");
            var now = DateTime.UtcNow;
            var key = new AccessKey
            {
                Id = Guid.NewGuid().ToString(),
                Name = clean,
                RoleId = role.Id,
                RoleName = role.Name,
                AllDevices = group is null,
                DeviceGroupId = group?.Id ?? string.Empty,
                DeviceGroupName = group?.Name ?? string.Empty,
                CreatedBy = "admin",
                Created = Timestamp.FromDateTime(now),
                Expires = Timestamp.FromDateTime(now.AddDays(request.Days)),
            };
            _accessKeys.Add(key);
            var secret = "oadm_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return Task.FromResult(new CreatedAccessKey { Key = key, Secret = secret });
        }
    }

    public Task<IReadOnlyList<AccessKey>> DeleteAccessKeyAsync(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (_accessKeys.RemoveAll(k => k.Id == id) == 0)
            {
                throw new RpcException(new Status(StatusCode.NotFound, "The access key no longer exists."));
            }

            return Task.FromResult(KeysNow());
        }
    }

    private IReadOnlyList<AccessKey> KeysNow() => [.. _accessKeys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)];

    private static RpcException KeyRefused(string field, string text) =>
        new(new Status(StatusCode.InvalidArgument, text), new Metadata { { "oadm-field", field } });

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

    public Task<McpSkillZip> GetMcpSkillAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("oadm/SKILL.md").Open());
            writer.Write("---\nname: oadm\ndescription: Fake skill.\n---\n");
        }

        return Task.FromResult(new McpSkillZip { FileName = "oadm-skill.zip", Content = Google.Protobuf.ByteString.CopyFrom(stream.ToArray()) });
    }

    private McpState McpStateNow()
    {
        var state = new McpState
        {
            Settings = _mcp.Clone(),
            Running = _mcp.Enabled,
            Status = _mcp.Enabled ? Oadm.Contracts.Mcp.McpTexts.Running(_mcp.Port) : Oadm.Contracts.Mcp.McpTexts.Off,
            PkiAvailable = true,
            Certificate = _mcp.Enabled ? "MCP Server Certificate (OADM Root CA FAKE), until 2036-11-14" : string.Empty, // PKI is asked only while on
            HasCaCertificate = _mcp.Enabled,
        };
        if (_mcp.Enabled)
        {
            state.CertificateNames.AddRange(["oadm-server.local", "10.0.0.17"]);
        }

        state.Interfaces.Add(new McpInterface { Id = "all", Label = "All interfaces", Name = "All interfaces" });
        state.Interfaces.Add(new McpInterface { Id = "eth0", Label = "Ethernet - 10.0.0.17", Name = "Ethernet", Addresses = { "10.0.0.17" } });
        return state;
    }
}
