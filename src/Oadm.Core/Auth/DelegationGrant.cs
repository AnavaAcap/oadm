using System.Collections.Frozen;
using System.Text.Json;

namespace Oadm.Core.Auth;

/// <summary>A physical device explicitly authorized for a connection. Replacing an address does not replace this identity.</summary>
public sealed record DelegatedDevice(Guid Id, string Serial);

/// <summary>
/// An immutable ceiling, never a role: effective access intersects this grant with the owner's current rights.
/// Unknown versions or malformed persisted grants fail closed. A connection cannot extend itself.
/// </summary>
public sealed record DelegationGrant(int Version, Guid OwnerId, IReadOnlyList<DelegatedDevice> Devices,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> Tools)
{
    public const int CurrentVersion = 1;
    public const int MaxDevices = 10_000;
    public const int DefaultDays = 30;

    public bool IsValid => Version == CurrentVersion && OwnerId != Guid.Empty
        && Devices is { Count: > 0 and <= MaxDevices } && Devices.All(d => d is not null && d.Id != Guid.Empty
            && d.Serial is { Length: > 0 and <= 64 } && d.Serial.All(char.IsAsciiLetterOrDigit))
        && Devices.Select(d => d.Id).Distinct().Count() == Devices.Count
        && Permissions is { Count: > 0 and <= 64 } && Permissions.All(ValidKey)
        && Tools is { Count: > 0 and <= 128 } && Tools.All(t => ValidKey(t) && t.StartsWith("oadm_", StringComparison.Ordinal));

    public PermissionSet Intersect(PermissionSet current) => PermissionSet.Of(Permissions.Where(current.Has));

    public FrozenDictionary<Guid, string> DevicePins() => Devices.ToFrozenDictionary(d => d.Id, d => d.Serial, EqualityComparer<Guid>.Default);

    public static DelegationGrant? Read(string? json)
    {
        try
        {
            return json is not null && JsonSerializer.Deserialize<DelegationGrant>(json) is { IsValid: true } grant ? grant : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidKey(string value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-');
}
