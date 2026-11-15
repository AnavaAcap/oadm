using System.Globalization;
using System.Text;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Security;
using Oadm.Server.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tools;

/// <summary>A secret a run made and offers once (a new access key): its file reference, name and end. Never part of the JSON.</summary>
/// <param name="FileRef">The reference of the one-time file (<see cref="ToolHandles.AddOffer"/>).</param>
/// <param name="Name">The file name ("oadm-key.txt").</param>
/// <param name="Title">What the card calls it ("Access key Lobby-bot").</param>
/// <param name="Expires">When the reference ends (UTC, ISO 8601).</param>
internal sealed record ToolSecretFile(string FileRef, string Name, string Title, string Expires);

/// <summary>
/// A secret that can be read once (docs/spec/mcp.md "Access keys"): held encrypted with the master key in server memory, its
/// clear bytes made only for the one read and wiped when the reader closes the stream. A second read is refused.
/// </summary>
internal sealed class OneTimeSecret
{
    public const string Taken = "This access key was fetched already. OADM cannot show it again: delete the key and create a new one.";

    private readonly Lock _gate = new();
    private readonly CredentialProtector _protector;
    private readonly byte[] _associated = Encoding.UTF8.GetBytes("one-time:" + Guid.NewGuid().ToString("N"));
    private byte[]? _blob;

    public OneTimeSecret(CredentialProtector protector, string value)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
        _blob = protector.Protect(value, _associated);
    }

    /// <summary>The value as a stream, once; NOT_FOUND <see cref="Taken"/> afterwards.</summary>
    public Task<Stream> OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        byte[] blob;
        lock (_gate)
        {
            blob = _blob ?? throw new RpcException(new Status(StatusCode.NotFound, Taken));
            _blob = null;
        }

        var bytes = Encoding.UTF8.GetBytes(_protector.Unprotect(blob, _associated));
        Array.Clear(blob);
        return Task.FromResult<Stream>(new WipedStream(bytes));
    }

    /// <summary>A file of the secret for <see cref="ToolHandles"/>: its bytes are made when the link or card fetches it, once.</summary>
    public PlannedDownload AsDownload(string fileName) => new(fileName, "text/plain", (_, ct) => OpenAsync(ct)) { Size = null };

    /// <summary>A read-only stream over the bytes that wipes them when it is closed.</summary>
    private sealed class WipedStream : MemoryStream
    {
        private readonly byte[] _bytes;

        public WipedStream(byte[] bytes)
            : base(bytes, writable: false) => _bytes = bytes;

        protected override void Dispose(bool disposing)
        {
            Array.Clear(_bytes);
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// The shared parts of the access tools (users, roles, device groups, access keys; docs/spec/mcp.md "Access"): the gRPC paths,
/// finding a user, role, group or key by its name or id the way an administrator names it, and the texts. Every tool calls
/// the Administrator-only services of the Permissions page in process as its caller, so only an Administrator (a user or a key
/// with that role) sees them, and the services' own checks, refusals and audit apply (decision 2026-11-15).
/// </summary>
internal static class AccessTools
{
    public static readonly string UserListPath = InProcessCalls.PathOf(Proto.UserService.Descriptor, "List");
    public static readonly string UserAddPath = InProcessCalls.PathOf(Proto.UserService.Descriptor, "Add");
    public static readonly string UserUpdatePath = InProcessCalls.PathOf(Proto.UserService.Descriptor, "Update");
    public static readonly string UserDeletePath = InProcessCalls.PathOf(Proto.UserService.Descriptor, "Delete");
    public static readonly string RoleMatrixPath = InProcessCalls.PathOf(Proto.RoleService.Descriptor, "GetMatrix");
    public static readonly string RoleSavePath = InProcessCalls.PathOf(Proto.RoleService.Descriptor, "SaveRoles");
    public static readonly string RoleDeletePath = InProcessCalls.PathOf(Proto.RoleService.Descriptor, "DeleteRole");
    public static readonly string GroupListPath = InProcessCalls.PathOf(Proto.DeviceGroupService.Descriptor, "List");
    public static readonly string GroupSavePath = InProcessCalls.PathOf(Proto.DeviceGroupService.Descriptor, "Save");
    public static readonly string GroupDeletePath = InProcessCalls.PathOf(Proto.DeviceGroupService.Descriptor, "Delete");
    public static readonly string GroupPreviewPath = InProcessCalls.PathOf(Proto.DeviceGroupService.Descriptor, "Preview");
    public static readonly string KeyListPath = InProcessCalls.PathOf(Proto.AccessKeyService.Descriptor, "List");
    public static readonly string KeyCreatePath = InProcessCalls.PathOf(Proto.AccessKeyService.Descriptor, "Create");
    public static readonly string KeyDeletePath = InProcessCalls.PathOf(Proto.AccessKeyService.Descriptor, "Delete");

    public const string UnknownUser = "No user has this name. See them with oadm_users_list.";
    public const string UnknownRole = "No role has this name. See them with oadm_roles_list.";
    public const string UnknownGroup = "No device group has this name. See them with oadm_device_groups_list.";
    public const string UnknownKey = "No access key has this name. See them with oadm_access_keys_list.";

    /// <summary>What <c>devices</c> takes for all devices.</summary>
    public const string AllDevices = "all";

    /// <summary>The name of the Administrator role as the role list shows it.</summary>
    public static string AdministratorName(Proto.RoleMatrix matrix) =>
        matrix.Roles.FirstOrDefault(r => r.Kind == Proto.RoleKind.Administrator)?.Name ?? "Administrator";

    public static Task<Proto.UserList> UsersAsync(ToolCall call) =>
        call.CallAsync<UserGrpcService, Proto.Empty, Proto.UserList>(UserListPath, new Proto.Empty(), (s, r, c) => s.List(r, c));

    public static Task<Proto.RoleMatrix> MatrixAsync(ToolCall call) =>
        call.CallAsync<RoleGrpcService, Proto.Empty, Proto.RoleMatrix>(RoleMatrixPath, new Proto.Empty(), (s, r, c) => s.GetMatrix(r, c));

    public static Task<Proto.DeviceGroupList> GroupsAsync(ToolCall call) =>
        call.CallAsync<DeviceGroupGrpcService, Proto.Empty, Proto.DeviceGroupList>(GroupListPath, new Proto.Empty(), (s, r, c) => s.List(r, c));

    public static Task<Proto.AccessKeyList> KeysAsync(ToolCall call) =>
        call.CallAsync<AccessKeyGrpcService, Proto.Empty, Proto.AccessKeyList>(KeyListPath, new Proto.Empty(), (s, r, c) => s.List(r, c));

    /// <summary>The user <paramref name="nameOrId"/> (its id, or its name in any case); a refusal at <paramref name="field"/> otherwise.</summary>
    public static Proto.UserInfo User(Proto.UserList users, string? nameOrId, string field = "user") =>
        Find(users.Users, nameOrId, u => u.Id, u => u.UserName) ?? throw Refusal(field, UnknownUser);

    public static Proto.RoleInfo Role(Proto.RoleMatrix matrix, string? nameOrId, string field = "role") =>
        Find(matrix.Roles, nameOrId, r => r.Id, r => r.Name) ?? throw Refusal(field, UnknownRole);

    public static Proto.DeviceGroupInfo Group(Proto.DeviceGroupList groups, string? nameOrId, string field = "group") =>
        Find(groups.Groups, nameOrId, g => g.Id, g => g.Name) ?? throw Refusal(field, UnknownGroup);

    public static Proto.AccessKey Key(Proto.AccessKeyList keys, string? nameOrId, string field = "key") =>
        Find(keys.Keys, nameOrId, k => k.Id, k => k.Name) ?? throw Refusal(field, UnknownKey);

    /// <summary>
    /// The devices of <c>devices</c>: <see cref="AllDevices"/>, or a device group by name or id (null: left out). The
    /// Administrator role always has all devices.
    /// </summary>
    public static DeviceScope? DevicesOf(ToolCall call, Proto.DeviceGroupList groups, string field = "devices")
    {
        ArgumentNullException.ThrowIfNull(call);
        var text = call.Text(field);
        if (text is null)
        {
            return null;
        }

        return string.Equals(text.Trim(), AllDevices, StringComparison.OrdinalIgnoreCase)
            ? DeviceScope.All
            : DeviceScope.Group(Guid.Parse(Group(groups, text, field).Id, CultureInfo.InvariantCulture));
    }

    /// <summary>"All devices", the group's name, or "No devices" (its group was deleted), as the Permissions page says.</summary>
    public static string DevicesText(bool allDevices, string groupName) =>
        allDevices ? DeviceGroupTexts.AllDevices : groupName.Length > 0 ? groupName : DeviceGroupTexts.NoDevices;

    /// <summary>The name of the devices <paramref name="scope"/> names.</summary>
    public static string DevicesText(DeviceScope scope, Proto.DeviceGroupList groups) =>
        scope.AllDevices ? DeviceGroupTexts.AllDevices
        : groups.Groups.FirstOrDefault(g => g.Id == scope.GroupId?.ToString())?.Name ?? DeviceGroupTexts.NoDevices;

    public static ToolException Refusal(string field, string message) => new(message, [new ToolFieldError(field, message)]);

    /// <summary>An item by its id (exact), else by its name in any case; null when none has it.</summary>
    private static T? Find<T>(IEnumerable<T> items, string? nameOrId, Func<T, string> id, Func<T, string> name)
        where T : class
    {
        var text = nameOrId?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var list = items as IReadOnlyCollection<T> ?? [.. items];
        return list.FirstOrDefault(i => string.Equals(id(i), text, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(i => string.Equals(name(i), text, StringComparison.OrdinalIgnoreCase));
    }
}
