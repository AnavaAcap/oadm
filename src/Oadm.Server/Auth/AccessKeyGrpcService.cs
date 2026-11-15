using System.Globalization;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Auth;

/// <summary>
/// gRPC AccessKeyService (docs/spec/mcp.md "Access keys"): the access keys the MCP server takes, Administrator only
/// (<see cref="AccessPolicy"/>; the Settings card AI, section MCP). A key has a name, a role, devices (all, or one device group)
/// and an end; it is an identity of its own. A new key is in the reply of Create only; the server keeps its SHA-256. Created
/// and deleted keys are audited by the administrator who did it, never with the key.
/// </summary>
public sealed class AccessKeyGrpcService(AccessKeyStore keys, AuditLog audit) : Proto.AccessKeyService.AccessKeyServiceBase
{
    /// <summary>The trailer of a refused create that names its field: name, role, devices or days.</summary>
    public const string FieldTrailer = "oadm-field";

    public override async Task<Proto.AccessKeyList> List(Proto.Empty request, ServerCallContext context) =>
        ToProto(await keys.ListAsync(context.CancellationToken).ConfigureAwait(false));

    public override async Task<Proto.CreatedAccessKey> Create(Proto.CreateAccessKeyRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        if (!Guid.TryParse(request.RoleId, out var roleId))
        {
            throw Refused(AccessKeyField.Role, AccessKeyTexts.ChooseRole);
        }

        Guid? groupId = null;
        if (!request.AllDevices && !string.IsNullOrEmpty(request.DeviceGroupId))
        {
            groupId = Guid.TryParse(request.DeviceGroupId, out var id) ? id : throw Refused(AccessKeyField.Devices, Core.DeviceGroups.DeviceGroupTexts.UnknownGroup);
        }

        try
        {
            var (key, info) = await keys.CreateAsync(request.Name, roleId, new DeviceScope(request.AllDevices, groupId), request.Days, caller.UserName,
                context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.AccessKeyCreated, info.Name, Detail(info), context.CancellationToken).ConfigureAwait(false);
            return new Proto.CreatedAccessKey { Key = ToProto(info), Secret = key };
        }
        catch (AccessKeyFieldException ex)
        {
            throw Refused(ex.Field, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
    }

    public override async Task<Proto.AccessKeyList> Delete(Proto.AccessKeyIdRequest request, ServerCallContext context)
    {
        var name = Guid.TryParse(request.Id, out var id) ? await keys.DeleteAsync(id, context.CancellationToken).ConfigureAwait(false) : null;
        if (name is null)
        {
            throw GrpcGuard.NotFound(AccessKeyTexts.NotFound);
        }

        await audit.WriteAsync(AuditActions.AccessKeyDeleted, name, null, CancellationToken.None).ConfigureAwait(false); // deleted: always audited
        return ToProto(await keys.ListAsync(context.CancellationToken).ConfigureAwait(false));
    }

    private static RpcException Refused(AccessKeyField field, string message) =>
        new(new Status(StatusCode.InvalidArgument, message), new Metadata { { FieldTrailer, field.ToString().ToLowerInvariant() } });

    /// <summary>"Role Operator, device group Lobby, until 2027-02-12" (what the key may do, never the key).</summary>
    private static string Detail(AccessKeyInfo key) =>
        "Role " + key.RoleName + ", " + (key.Devices.AllDevices ? "all devices" : "device group " + key.DeviceGroupName)
        + ", until " + key.ExpiresUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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
            Id = key.Id.ToString(),
            Name = key.Name,
            RoleId = key.RoleId.ToString(),
            RoleName = key.RoleName,
            AllDevices = key.Devices.AllDevices,
            DeviceGroupId = key.Devices.AllDevices ? string.Empty : key.Devices.GroupId?.ToString() ?? string.Empty,
            DeviceGroupName = key.DeviceGroupName,
            CreatedBy = key.CreatedBy,
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
