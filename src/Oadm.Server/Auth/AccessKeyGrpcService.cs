using System.Globalization;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Auth;

/// <summary>
/// gRPC AccessKeyService (docs/spec/mcp.md "Access keys"): the access keys AI agents send to the MCP server. Every user lists
/// and deletes their own; creating one needs "Use AI agents" (<see cref="HostPermissions.McpUse"/>, <see cref="AccessPolicy"/>);
/// administrators list and delete every user's (Permissions page). A new key is in the reply of Create only; the server keeps
/// its SHA-256. Created and deleted keys are audited, never with the key.
/// </summary>
public sealed class AccessKeyGrpcService(AuthTokenStore tokens, AuditLog audit) : Proto.AccessKeyService.AccessKeyServiceBase
{
    public override async Task<Proto.AccessKeyList> ListMine(Proto.Empty request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        return ToProto(await tokens.ListAccessKeysAsync(caller.UserId, context.CancellationToken).ConfigureAwait(false));
    }

    public override async Task<Proto.CreatedAccessKey> Create(Proto.CreateAccessKeyRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        try
        {
            var (key, info) = await tokens.CreateAccessKeyAsync(caller.UserId, caller.UserName, request.Name, request.Days, request.ReadOnly,
                caller.ClientAddress, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.AccessKeyCreated, info.Name, Detail(info), context.CancellationToken).ConfigureAwait(false);
            return new Proto.CreatedAccessKey { Key = ToProto(info), Secret = key };
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
    }

    public override async Task<Proto.AccessKeyList> Delete(Proto.AccessKeyIdRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        await DeleteAsync(request.Id, caller.UserId, context.CancellationToken).ConfigureAwait(false);
        return ToProto(await tokens.ListAccessKeysAsync(caller.UserId, context.CancellationToken).ConfigureAwait(false));
    }

    public override async Task<Proto.AccessKeyList> ListAll(Proto.Empty request, ServerCallContext context) =>
        ToProto(await tokens.ListAccessKeysAsync(null, context.CancellationToken).ConfigureAwait(false));

    public override async Task<Proto.AccessKeyList> DeleteAny(Proto.AccessKeyIdRequest request, ServerCallContext context)
    {
        await DeleteAsync(request.Id, null, context.CancellationToken).ConfigureAwait(false);
        return ToProto(await tokens.ListAccessKeysAsync(null, context.CancellationToken).ConfigureAwait(false));
    }

    private async Task DeleteAsync(string id, Guid? userId, CancellationToken ct)
    {
        var deleted = await tokens.DeleteAccessKeyAsync(id ?? string.Empty, userId, ct).ConfigureAwait(false)
            ?? throw GrpcGuard.NotFound(AccessKeyTexts.NotFound);
        await audit.WriteAsync(AuditActions.AccessKeyDeleted, deleted.UserName + ": " + deleted.Name, null, ct).ConfigureAwait(false);
    }

    /// <summary>"Read only, until 2027-02-12" (what the key may do, never the key).</summary>
    private static string Detail(AccessKeyInfo key) =>
        (key.ReadOnly ? "Read only, " : string.Empty) + "until " + key.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Proto.AccessKeyList ToProto(IReadOnlyList<AccessKeyInfo> keys)
    {
        var reply = new Proto.AccessKeyList();
        reply.Keys.AddRange(keys.Select(ToProto));
        return reply;
    }

    private static Proto.AccessKey ToProto(AccessKeyInfo key)
    {
        var proto = new Proto.AccessKey
        {
            Id = key.Id,
            UserName = key.UserName,
            Name = key.Name,
            ReadOnly = key.ReadOnly,
            Created = Utc(key.CreatedUtc),
            Expires = Utc(key.ExpiresUtc),
        };
        if (key.LastUsedUtc is { } used)
        {
            proto.LastUsed = Utc(used);
        }

        return proto;
    }

    private static Timestamp Utc(DateTime value) => Timestamp.FromDateTime(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
