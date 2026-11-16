using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Shared;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>
/// A row of the table as the page shows it: the texts and the colour of the status chip. One per VPN app and device,
/// updated in place when the server sends the device's rows again (the selection stays).
/// </summary>
public sealed partial class FleetRowView : ObservableObject
{
    public FleetRowView(FleetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        DeviceId = row.DeviceId;
        Kind = row.Vpn;
        Row = row;
        AddressSortKey = AddressSortKeys.Of(row.Address);
    }

    public Guid DeviceId { get; }

    /// <summary>The app of the row; null for a device without a VPN app or one that was not read.</summary>
    public VpnKind? Kind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Address), nameof(Model), nameof(AppVersion), nameof(Update), nameof(UpdateAvailable), nameof(VpnAddress),
        nameof(Detail), nameof(StateText), nameof(IsOk), nameof(IsWarning), nameof(IsError))]
    public partial FleetRow Row { get; private set; }

    public string Address => Row.Address;

    /// <summary>The address sorted numerically ("010.000.000.048").</summary>
    [ObservableProperty]
    public partial string AddressSortKey { get; private set; }

    public string Model => Row.Model;

    public string Vpn => Kind is { } kind ? VpnApps.NameOf(kind) : "";

    public string AppVersion => Row.AppVersion ?? "";

    /// <summary>"1.104.7" when it is newer than the installed version, else empty.</summary>
    public string Update => Row.UpdateAvailable ? Row.LatestVersion! : "";

    public bool UpdateAvailable => Row.UpdateAvailable;

    public string VpnAddress => Row.VpnAddress ?? "";

    public string Detail => Row.Error ?? Row.Detail ?? "";

    public string StateText => Row.Error is not null ? "Not read" : TextOf(Row.State);

    public bool IsOk => Row.Error is null && Row.State == VpnState.Connected;

    public bool IsWarning => Row.Error is null && Row.State is VpnState.Connecting or VpnState.NotSetUp or VpnState.AppStopped;

    public bool IsError => Row.Error is not null || Row.State == VpnState.NotConnected;

    /// <summary>What the page's search looks in.</summary>
    public bool Matches(string text) =>
        Address.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Model.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Vpn.Contains(text, StringComparison.OrdinalIgnoreCase)
        || StateText.Contains(text, StringComparison.OrdinalIgnoreCase)
        || VpnAddress.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>The device's row came again from the server.</summary>
    public void Apply(FleetRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row == Row)
        {
            return;
        }

        if (row.Address != Row.Address)
        {
            AddressSortKey = AddressSortKeys.Of(row.Address);
        }

        Row = row;
    }

    public static string TextOf(VpnState state) => state switch
    {
        VpnState.Connected => "Connected",
        VpnState.Connecting => "Connecting",
        VpnState.NotConnected => "Not connected",
        VpnState.NotSetUp => "Not set up",
        VpnState.AppStopped => "App stopped",
        VpnState.NoVpnApp => "No VPN app",
        _ => "Unknown",
    };
}

/// <summary>A choice of an app list.</summary>
public sealed record VpnChoice(VpnKind Kind, string Name)
{
    public override string ToString() => Name;
}
