using System.Text;

using Grpc.Core;

using Oadm.Contracts.Mcp;
using Oadm.Core.Auth;
using Oadm.Core.Settings;
using Oadm.Sdk.Network;
using Oadm.Server.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Mcp;

/// <summary>
/// gRPC McpService, the Settings card AI, section MCP (docs/spec/mcp.md "Transport and certificate"): the state for every signed-in
/// user, Turn on or off, the port and Listen on for administrators (audited as "Changed MCP server"), and the CA certificates of
/// the server's certificate for the agent's computer.
/// </summary>
public sealed class McpGrpcService(McpListener listener, ServerSettingsStore store, AuditLog audit) : Proto.McpService.McpServiceBase
{
    /// <summary>The file name of the CA download (the name the docs use in <c>curl --cacert</c>).</summary>
    public const string CaFileName = "oadm-ca.pem";

    public override Task<Proto.McpState> Get(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(ToProto(listener.Status, AuthInterceptor.CallerOf(context).IsAdmin));

    public override async Task<Proto.McpState> Set(Proto.McpSettings request, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (McpSettings.PortError(request.Port) is { } port)
        {
            throw GrpcGuard.InvalidArgument(port);
        }

        var listenOn = McpSettings.Normal(request.ListenOn);
        var interfaces = listener.Interfaces();
        var before = await McpSettings.LoadAsync(store, ct).ConfigureAwait(false);
        if (listenOn is not null && !interfaces.Any(i => i.Id == listenOn) && !string.Equals(listenOn, before.ListenOn, StringComparison.Ordinal))
        {
            throw GrpcGuard.InvalidArgument(McpTexts.UnknownInterface); // a stored interface that is gone may stay chosen
        }

        if (request.Enabled && !listener.Status.PkiAvailable)
        {
            throw GrpcGuard.FailedPrecondition(McpTexts.NoPki);
        }

        var after = new McpSettings { Enabled = request.Enabled, Port = request.Port, ListenOn = listenOn };
        await after.SaveAsync(store, ct).ConfigureAwait(false);
        string NameOf(string? id) => McpSettings.Normal(id) is not { } chosen ? InterfaceOptions.AllLabel : interfaces.FirstOrDefault(i => i.Id == chosen)?.Name ?? chosen;
        await audit.WriteAsync(AuditActions.McpChanged, "MCP server", McpSettings.Changes(before, after, NameOf), ct).ConfigureAwait(false);
        await listener.ApplyAsync(ct).ConfigureAwait(false);
        return ToProto(listener.Status, admin: true);
    }

    public override Task<Proto.McpCaCertificate> GetCaCertificate(Proto.Empty request, ServerCallContext context)
    {
        var cas = listener.Status.CaCertificates;
        if (cas.Count == 0)
        {
            throw GrpcGuard.NotFound(listener.Status.Certificate is null ? McpTexts.NoCaCertificate : McpTexts.NoIssuer);
        }

        var pem = new StringBuilder();
        foreach (var der in cas)
        {
            pem.Append(new string(System.Security.Cryptography.PemEncoding.Write("CERTIFICATE", der))).Append('\n');
        }

        return Task.FromResult(new Proto.McpCaCertificate { FileName = CaFileName, Pem = pem.ToString() });
    }

    public override Task<Proto.McpSkillZip> GetSkill(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(new Proto.McpSkillZip { FileName = McpSkill.ZipFileName, Content = Google.Protobuf.ByteString.CopyFrom(McpSkill.Zip()) });

    private Proto.McpState ToProto(McpStatus status, bool admin)
    {
        var state = new Proto.McpState
        {
            Settings = new Proto.McpSettings { Enabled = status.Settings.Enabled, Port = status.Settings.Port, ListenOn = status.Settings.ListenOn ?? string.Empty },
            Running = status.Running,
            Status = status.Text,
            PkiAvailable = status.PkiAvailable,
            Certificate = status.Certificate ?? string.Empty,
            HasCaCertificate = status.CaCertificates.Count > 0,
        };
        state.CertificateNames.AddRange(status.CertificateNames);
        if (admin)
        {
            state.Interfaces.Add(new Proto.McpInterface { Id = InterfaceOptions.AllId, Label = InterfaceOptions.AllLabel, Name = InterfaceOptions.AllLabel });
            foreach (var nic in listener.Interfaces())
            {
                var option = InterfaceOptions.From(nic);
                var proto = new Proto.McpInterface { Id = option.Id, Label = option.Label, Name = option.Name };
                proto.Addresses.AddRange(option.Addresses);
                state.Interfaces.Add(proto);
            }
        }

        return state;
    }
}
