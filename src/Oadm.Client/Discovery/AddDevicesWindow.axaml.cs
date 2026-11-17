using System.Collections.Specialized;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Threading;

namespace Oadm.Client.Discovery;

/// <summary>The add devices page window. Glue only: open/close, keyboard focus for the inputs, the columns shown and their width.</summary>
public partial class AddDevicesWindow : Window
{
    private AddDevicesViewModel? _vm;

    public AddDevicesWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= OnCloseRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Rows.CollectionChanged -= OnRowsChanged;
        }

        _vm = DataContext as AddDevicesViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += OnCloseRequested;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Rows.CollectionChanged += OnRowsChanged;
            ShowSiteColumn(_vm.ShowSiteColumn);
            FitAddressColumn();
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_vm is null)
        {
            return;
        }

        // The technician types right away: focus the input of the mode.
        (_vm.IsRangeMode ? RangeFromBox : _vm.IsManualMode ? AddressBox : null)?.Focus();
        await _vm.OpenAsync();
    }

    private void OnCloseRequested(object? sender, bool added) => Close(added);

    /// <summary>The Site column (a grid column is not in the tree: no binding to the view model).</summary>
    private void ShowSiteColumn(bool show)
    {
        foreach (DataGridColumn column in DeviceList.Columns)
        {
            if (Equals(column.Header, "Site"))
            {
                column.IsVisible = show;
            }
        }
    }

    /// <summary>Address column while host names can show.</summary>
    public const double HostNameWidth = 220;

    /// <summary>The Address column is never narrower than its header.</summary>
    private const double MinAddressWidth = 80;

    /// <summary>The cell text's margins (theme: <c>DataGridCell TextBlock</c> 10 px each side) and a little room.</summary>
    private const double CellMargins = 2 * 10 + 4;

    /// <summary>The longest IP address in the list so far (by characters): the Address column fits it.</summary>
    private string _longestIp = "";

    /// <summary>
    /// The Address column: wide while host names can show; otherwise as wide as the longest IP address in the list
    /// ("10.0.0.92" needs less room than "192.168.100.254"). O(1) per new row, O(n) once when DNS names are switched.
    /// </summary>
    private void FitAddressColumn()
    {
        if (_vm is null)
        {
            return;
        }

        _longestIp = "";
        foreach (DiscoveredRowViewModel row in _vm.Rows)
        {
            Longer(row.IpAddress);
        }

        SizeAddressColumn();
    }

    private bool Longer(string ip)
    {
        if (ip.Length <= _longestIp.Length)
        {
            return false;
        }

        _longestIp = ip;
        return true;
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || e.Action == NotifyCollectionChangedAction.Remove)
        {
            FitAddressColumn();
            return;
        }

        bool longer = false;
        foreach (DiscoveredRowViewModel row in e.NewItems?.OfType<DiscoveredRowViewModel>() ?? [])
        {
            longer |= Longer(row.IpAddress);
        }

        if (longer)
        {
            SizeAddressColumn();
        }
    }

    private void SizeAddressColumn()
    {
        double width = _vm is { ShowsHostNames: true } ? HostNameWidth : Math.Max(MinAddressWidth, TextWidth(_longestIp) + CellMargins);
        foreach (DataGridColumn column in DeviceList.Columns)
        {
            if (Equals(column.Header, "Address"))
            {
                column.Width = new DataGridLength(Math.Ceiling(width));
            }
        }
    }

    /// <summary>The width of a text in the grid's cell font.</summary>
    private double TextWidth(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        double fontSize = this.TryFindResource("Oadm.FontSizeBody", out object? size) && size is double s ? s : 12;
        using var layout = new Avalonia.Media.TextFormatting.TextLayout(text, new Avalonia.Media.Typeface(DeviceList.FontFamily), fontSize, null);
        return layout.WidthIncludingTrailingWhitespace;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddDevicesViewModel.ShowSiteColumn) && _vm is not null)
        {
            ShowSiteColumn(_vm.ShowSiteColumn);
        }

        if (e.PropertyName == nameof(AddDevicesViewModel.ShowsHostNames) && _vm is not null)
        {
            FitAddressColumn();
        }

        if (e.PropertyName == nameof(AddDevicesViewModel.Editor) && _vm is not null)
        {
            TextBox? target = _vm.IsLoginEditorOpen ? LoginPasswordBox : _vm.IsPasswordEditorOpen ? NewPasswordBox : null;
            if (target is not null)
            {
                DiscoveredRowViewModel? row = _vm.EditorRow;
                Dispatcher.UIThread.Post(() =>
                {
                    if (row is not null)
                    {
                        DeviceList.ScrollIntoView(row, null); // keep the edited device visible above the editor
                    }

                    target.Focus();
                });
            }
        }
    }
}
