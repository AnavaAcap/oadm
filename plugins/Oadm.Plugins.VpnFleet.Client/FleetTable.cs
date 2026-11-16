using System.Globalization;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>The counts of the page's summary line, kept up to date per changed row (never counted over all rows).</summary>
public sealed class FleetCounts
{
    public int Devices { get; internal set; }

    public int Connected { get; private set; }

    public int NotConnected { get; private set; }

    public int NoApp { get; private set; }

    public int NotRead { get; private set; }

    public int Updates { get; private set; }

    /// <summary>Counts a row in (+1) or out (-1).</summary>
    internal void Add(FleetRow row, int sign)
    {
        if (row.Error is not null)
        {
            NotRead += sign;
        }
        else if (row.State == VpnState.Connected)
        {
            Connected += sign;
        }
        else if (row.State == VpnState.NoVpnApp)
        {
            NoApp += sign;
        }
        else
        {
            NotConnected += sign;
        }

        if (row.UpdateAvailable)
        {
            Updates += sign;
        }
    }

    internal void Reset()
    {
        Devices = Connected = NotConnected = NoApp = NotRead = Updates = 0;
    }

    public static FleetCounts Of(IReadOnlyList<FleetRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var counts = new FleetCounts { Devices = rows.Select(r => r.DeviceId).Distinct().Count() };
        foreach (var row in rows)
        {
            counts.Add(row, 1);
        }

        return counts;
    }

    /// <summary>"12 devices: 9 connected, 2 not connected, 1 without a VPN app. 3 updates".</summary>
    public string Text(bool nothingChosen)
    {
        if (Devices == 0)
        {
            return nothingChosen ? "Choose devices to see their VPN." : "Click Refresh to read the devices.";
        }

        var parts = new List<string>();
        void Count(int n, string text)
        {
            if (n > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{n:N0} {text}"));
            }
        }

        Count(Connected, "connected");
        Count(NotConnected, "not connected");
        Count(NoApp, "without a VPN app");
        Count(NotRead, "not read");
        return (Devices == 1 ? "1 device" : string.Create(CultureInfo.CurrentCulture, $"{Devices:N0} devices"))
            + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "")
            + (Updates == 0 ? "" : Updates == 1 ? ". 1 update" : string.Create(CultureInfo.CurrentCulture, $". {Updates:N0} updates"));
    }
}

/// <summary>
/// The page's copy of the server's table: the rows per device, one view per row, updated in place when a device's rows
/// come again (O(1) per row) and only rebuilt (one reset, sorted by address) when rows come or go or the search changes.
/// Every device remembers the version of its rows, so a page of the first read and the events of the same time can
/// arrive in any order.
/// </summary>
public sealed class FleetTable
{
    private readonly Dictionary<Guid, Entry> _devices = [];
    private int _floor = int.MinValue;
    private string _search = "";

    /// <summary>The rows the search shows, sorted by address and app; bound to the grid.</summary>
    public RangeObservableCollection<FleetRowView> Shown { get; } = [];

    public FleetCounts Counts { get; } = new();

    /// <summary>The device's rows; false when the device is not in the table (not read).</summary>
    public bool TryGetRows(Guid deviceId, out IReadOnlyList<FleetRowView> rows)
    {
        if (_devices.TryGetValue(deviceId, out var entry) && entry.Views.Count > 0)
        {
            rows = entry.Views;
            return true;
        }

        rows = [];
        return false;
    }

    /// <summary>True when the view is still a row of the table.</summary>
    public bool Contains(FleetRowView view) =>
        view is not null && _devices.TryGetValue(view.DeviceId, out var entry) && entry.Views.Contains(view);

    /// <summary>
    /// A whole table (the pages of a read, each with its version): replaces every row; the views of the same device and
    /// app are kept, so the selection stays. One reset.
    /// </summary>
    public void Replace(IEnumerable<FleetState> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var old = new Dictionary<Guid, Entry>(_devices);
        _devices.Clear();
        Counts.Reset();
        _floor = int.MinValue;
        foreach (var page in pages)
        {
            foreach (var group in GroupByDevice(page.Rows))
            {
                var entry = new Entry(page.Version);
                _devices[group.Key] = entry;
                entry.Views = ViewsOf(group.Value, old.GetValueOrDefault(group.Key)?.Views);
                Count(entry, 1);
            }
        }

        Rebuild();
    }

    /// <summary>The events of a burst: the devices' rows change in place; one reset only when rows came or went. True when it reset.</summary>
    public bool Apply(IEnumerable<FleetRowsEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var structural = false;
        var changed = false;
        foreach (var item in events)
        {
            if (item.Version <= _floor)
            {
                continue;
            }

            var rows = GroupByDevice(item.Rows);
            foreach (var id in item.Devices)
            {
                var (rowChanged, rowStructural) = Set(id, item.Version, rows.GetValueOrDefault(id) ?? []);
                changed |= rowChanged;
                structural |= rowStructural;
            }
        }

        // A changed row may now match the search or no longer: the shown rows follow it.
        if (structural || (changed && _search.Length > 0))
        {
            Rebuild();
            return true;
        }

        return false;
    }

    /// <summary>The table was emptied at <paramref name="version"/>: every older row goes, older events are ignored.</summary>
    public void Clear(int version)
    {
        if (version <= _floor)
        {
            return;
        }

        _floor = version;
        foreach (var (id, entry) in _devices.Where(d => d.Value.Version <= version).ToList())
        {
            Count(entry, -1);
            _devices.Remove(id);
        }

        Rebuild();
    }

    /// <summary>Shows only the rows that contain <paramref name="search"/> (address, model, app, status, VPN address, details).</summary>
    public void Filter(string search)
    {
        _search = (search ?? "").Trim();
        Rebuild();
    }

    private (bool Changed, bool Structural) Set(Guid id, int version, List<FleetRow> rows)
    {
        if (!_devices.TryGetValue(id, out var entry))
        {
            entry = new Entry(version);
            _devices[id] = entry; // also without rows: an older event of the device stays ignored
            if (rows.Count == 0)
            {
                return (false, false);
            }

            entry.Views = ViewsOf(rows, null);
            Count(entry, 1);
            return (true, true);
        }

        if (entry.Version >= version)
        {
            return (false, false);
        }

        entry.Version = version;
        Count(entry, -1);
        var same = entry.Views.Count == rows.Count;
        for (var i = 0; same && i < rows.Count; i++)
        {
            same = entry.Views[i].Kind == rows[i].Vpn && entry.Views[i].Address == rows[i].Address; // a new address sorts anew
        }

        if (same)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                entry.Views[i].Apply(rows[i]);
            }
        }
        else
        {
            entry.Views = ViewsOf(rows, entry.Views);
        }

        Count(entry, 1);
        return (true, !same);
    }

    private void Count(Entry entry, int sign)
    {
        if (entry.Views.Count == 0)
        {
            return;
        }

        Counts.Devices += sign;
        foreach (var view in entry.Views)
        {
            Counts.Add(view.Row, sign);
        }
    }

    /// <summary>The views of a device's rows; a view of the same app is kept and updated.</summary>
    private static List<FleetRowView> ViewsOf(List<FleetRow> rows, List<FleetRowView>? old)
    {
        var views = new List<FleetRowView>(rows.Count);
        foreach (var row in rows)
        {
            if (old?.Find(v => v.Kind == row.Vpn) is { } kept)
            {
                kept.Apply(row);
                views.Add(kept);
            }
            else
            {
                views.Add(new FleetRowView(row));
            }
        }

        return views;
    }

    private static Dictionary<Guid, List<FleetRow>> GroupByDevice(IReadOnlyList<FleetRow> rows)
    {
        var byDevice = new Dictionary<Guid, List<FleetRow>>();
        foreach (var row in rows)
        {
            if (!byDevice.TryGetValue(row.DeviceId, out var list))
            {
                byDevice[row.DeviceId] = list = new List<FleetRow>(1);
            }

            list.Add(row);
        }

        return byDevice;
    }

    private void Rebuild()
    {
        IEnumerable<FleetRowView> rows = _devices.Values.SelectMany(e => e.Views);
        if (_search.Length > 0)
        {
            rows = rows.Where(r => r.Matches(_search));
        }

        Shown.ReplaceAll(rows.OrderBy(r => r.AddressSortKey, StringComparer.Ordinal).ThenBy(r => r.Kind));
    }

    private sealed class Entry(int version)
    {
        public int Version { get; set; } = version;

        public List<FleetRowView> Views { get; set; } = [];
    }
}
