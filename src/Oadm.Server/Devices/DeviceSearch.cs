using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Devices;

/// <summary>
/// One <c>DeviceService.Search</c> (docs/spec/grpc.md, docs/spec/mcp.md "Devices and the device filter"): the devices of the
/// caller's view are passed through <see cref="Add"/> once; it counts the matches (and a field's values when asked) and keeps
/// only the page, the rows right after the cursor in the sort order (a bounded set: memory never grows with the device
/// count). The cursor is the last row's sort key and id, so a device added, removed or changed elsewhere never moves the
/// rows between pages.
/// </summary>
public sealed class DeviceSearch
{
    /// <summary>The most rows of one page.</summary>
    public const int MaxRows = 500;

    /// <summary>The site of devices on the server's own network.</summary>
    public const string ThisServer = "This server";

    /// <summary>The value of devices without one (model, AXIS OS) in the counts.</summary>
    public const string UnknownValue = "Unknown";

    /// <summary>The tag count of devices without a tag.</summary>
    public const string NoTag = "No tag";

    public static IReadOnlyList<string> Sorts { get; } = ["address", "host_name", "upnp_name", "model", "firmware", "status", "last_seen"];

    public static IReadOnlyList<string> GroupBys { get; } = ["status", "model", "firmware", "site", "tag", "category"];

    private readonly Filter _filter;
    private readonly Func<Device, string> _key;
    private readonly bool _descending;
    private readonly (string Key, string Id)? _after;
    private readonly int _limit;
    private readonly string? _groupBy;
    private readonly SortedSet<Row> _page;
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    private DeviceSearch(Proto.SearchDevicesRequest request)
    {
        _filter = new Filter(request.Filter ?? new Proto.DeviceFilter());
        var sort = string.IsNullOrEmpty(request.Sort) ? "address" : request.Sort;
        _descending = sort.StartsWith('-');
        _key = KeyOf(sort.TrimStart('-')) ?? throw new ArgumentException("Choose a sort: " + string.Join(", ", Sorts) + ".");
        if (request.Limit is < 0 or > MaxRows)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Ask for 0 to {MaxRows} rows."));
        }

        _limit = request.Limit;
        if (!string.IsNullOrEmpty(request.GroupBy))
        {
            _groupBy = GroupBys.Contains(request.GroupBy, StringComparer.Ordinal) ? request.GroupBy
                : throw new ArgumentException("Count by one of: " + string.Join(", ", GroupBys) + ".");
        }

        if (!string.IsNullOrEmpty(request.AfterId))
        {
            _after = (request.AfterKey ?? string.Empty, request.AfterId);
        }

        _page = new SortedSet<Row>(Comparer<Row>.Create(Compare));
    }

    /// <summary>The search of <paramref name="request"/>; <see cref="ArgumentException"/> with a plain text for a value it does not know.</summary>
    public static DeviceSearch From(Proto.SearchDevicesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new DeviceSearch(request);
    }

    /// <summary>The ids the filter names, or null for every device (the service then reads only these).</summary>
    public IReadOnlyList<Guid>? Ids => _filter.Ids;

    /// <summary>True when only the number of every device of the view is asked for (no filter, no rows, no counts).</summary>
    public bool CountsOnly => _filter.IsEmpty && _limit == 0 && _groupBy is null;

    /// <summary>Devices that matched.</summary>
    public int Total { get; private set; }

    /// <summary>Devices that matched after the cursor (the page and the rest); <see cref="Total"/> without a cursor.</summary>
    public int AfterCursor { get; private set; }

    /// <summary>Looks at one device of the view.</summary>
    public void Add(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!_filter.Matches(device))
        {
            return;
        }

        Total++;
        if (_groupBy is not null)
        {
            foreach (var value in ValuesOf(device, _groupBy))
            {
                _counts[value] = _counts.GetValueOrDefault(value) + 1;
            }
        }

        var row = new Row(device, _key(device), device.Id.ToString());
        if (_after is { } after && Compare(row.Key, row.Id, after.Key, after.Id) <= 0)
        {
            return; // on a page before
        }

        AfterCursor++;
        if (_limit == 0)
        {
            return;
        }

        if (_page.Count < _limit)
        {
            _page.Add(row);
        }
        else if (Compare(row, _page.Max!) < 0)
        {
            _page.Remove(_page.Max!);
            _page.Add(row);
        }
    }

    /// <summary>The rows of the page in order, each with its sort key.</summary>
    public IReadOnlyList<(Device Device, string Key)> Page() => [.. _page.Select(r => (r.Device, r.Key))];

    /// <summary>The counts of the asked field, the most devices first, then by value.</summary>
    public IReadOnlyList<(string Value, int Count)> Counts() =>
        [.. _counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => (p.Key, p.Value))];

    /// <summary>The text of a status in the filter and the answers ("Ok", "CredentialsRequired").</summary>
    public static string StatusText(DeviceStatus status) => Enum.IsDefined(status) ? status.ToString() : nameof(DeviceStatus.Unknown);

    /// <summary>The text of a category ("Camera", "IoModule").</summary>
    public static string CategoryText(DeviceCategory category) => Enum.IsDefined(category) ? category.ToString() : nameof(DeviceCategory.Unknown);

    private int Compare(Row a, Row b) => Compare(a.Key, a.Id, b.Key, b.Id);

    private int Compare(string keyA, string idA, string keyB, string idB)
    {
        var byKey = StringComparer.OrdinalIgnoreCase.Compare(keyA, keyB);
        return byKey != 0 ? (_descending ? -byKey : byKey) : StringComparer.Ordinal.Compare(idA, idB);
    }

    private static Func<Device, string>? KeyOf(string sort) => sort switch
    {
        "address" => d => AddressKey(d.Address),
        "host_name" => d => d.HostName ?? string.Empty,
        "upnp_name" => d => d.UpnpFriendlyName ?? string.Empty,
        "model" => d => d.Model ?? string.Empty,
        "firmware" => d => FirmwareVersions.SortKey(d.FirmwareVersion ?? string.Empty),
        "status" => d => StatusText(d.Status),
        "last_seen" => d => d.LastSeenUtc is { } seen ? seen.Ticks.ToString("D20", CultureInfo.InvariantCulture) : string.Empty,
        _ => null,
    };

    /// <summary>IPv4 addresses in number order (10.0.0.2 before 10.0.0.10), names and IPv6 after them.</summary>
    private static string AddressKey(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
            ? "0" + string.Concat(ip.GetAddressBytes().Select(b => b.ToString("D3", CultureInfo.InvariantCulture)))
            : "1" + address;

    private static IEnumerable<string> ValuesOf(Device device, string groupBy)
    {
        switch (groupBy)
        {
            case "model":
                yield return string.IsNullOrEmpty(device.Model) ? UnknownValue : device.Model;
                break;
            case "firmware":
                yield return string.IsNullOrEmpty(device.FirmwareVersion) ? UnknownValue : device.FirmwareVersion;
                break;
            case "site":
                yield return SiteOf(device);
                break;
            case "category":
                yield return CategoryText(device.Category);
                break;
            case "tag":
                if (device.Tags.Count == 0)
                {
                    yield return NoTag;
                }

                foreach (var tag in device.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    yield return tag;
                }

                break;
            default:
                yield return StatusText(device.Status);
                break;
        }
    }

    private static string SiteOf(Device device) => string.IsNullOrEmpty(device.SiteName) ? ThisServer : device.SiteName;

    private sealed record Row(Device Device, string Key, string Id);

    /// <summary>The filter of a search: every field optional, combined with AND, a list matches any of its values.</summary>
    private sealed class Filter
    {
        private readonly HashSet<Guid>? _ids;
        private readonly string? _search;
        private readonly HashSet<string>? _tags;
        private readonly HashSet<string>? _sites;
        private readonly HashSet<string>? _models;
        private readonly string? _below;
        private readonly string? _atLeast;
        private readonly HashSet<DeviceStatus>? _statuses;
        private readonly HashSet<DeviceCategory>? _categories;

        public Filter(Proto.DeviceFilter filter)
        {
            if (filter.Ids.Count > 0)
            {
                _ids = [];
                foreach (var id in filter.Ids)
                {
                    _ids.Add(Guid.TryParse(id, out var parsed) ? parsed : throw new ArgumentException("A device id is not valid."));
                }

                Ids = [.. _ids];
            }

            _search = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
            _tags = Set(filter.Tags);
            _sites = Set(filter.Sites);
            _models = Set(filter.Models);
            _below = string.IsNullOrWhiteSpace(filter.FirmwareBelow) ? null : filter.FirmwareBelow.Trim();
            _atLeast = string.IsNullOrWhiteSpace(filter.FirmwareAtLeast) ? null : filter.FirmwareAtLeast.Trim();
            _statuses = filter.Statuses.Count > 0 ? [.. filter.Statuses.Select(s => (DeviceStatus)(int)s)] : null;
            _categories = filter.Categories.Count > 0 ? [.. filter.Categories.Select(c => (DeviceCategory)(int)c)] : null;
        }

        public IReadOnlyList<Guid>? Ids { get; }

        public bool IsEmpty => _ids is null && _search is null && _tags is null && _sites is null && _models is null && _below is null
            && _atLeast is null && _statuses is null && _categories is null;

        public bool Matches(Device device)
        {
            if ((_ids is not null && !_ids.Contains(device.Id))
                || (_statuses is not null && !_statuses.Contains(device.Status))
                || (_categories is not null && !_categories.Contains(device.Category))
                || (_models is not null && !_models.Contains(device.Model ?? string.Empty))
                || (_sites is not null && !_sites.Contains(SiteOf(device)))
                || (_tags is not null && !device.Tags.Any(_tags.Contains)))
            {
                return false;
            }

            if (_below is not null && (string.IsNullOrEmpty(device.FirmwareVersion) || FirmwareVersions.Compare(device.FirmwareVersion, _below) >= 0))
            {
                return false;
            }

            if (_atLeast is not null && (string.IsNullOrEmpty(device.FirmwareVersion) || FirmwareVersions.Compare(device.FirmwareVersion, _atLeast) < 0))
            {
                return false;
            }

            return _search is null || Contains(device.Address) || Contains(device.HostName) || Contains(device.UpnpFriendlyName)
                || Contains(device.Serial) || Contains(device.Model) || Contains(device.FirmwareVersion) || Contains(device.SiteName)
                || device.Tags.Any(Contains);
        }

        private bool Contains(string? value) => value is not null && value.Contains(_search!, StringComparison.OrdinalIgnoreCase);

        private static HashSet<string>? Set(Google.Protobuf.Collections.RepeatedField<string> values) =>
            values.Count > 0 ? values.Select(v => v.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
    }
}

/// <summary>AXIS OS versions ("12.11.77") compared by their numbers.</summary>
public static class FirmwareVersions
{
    /// <summary>Negative when <paramref name="a"/> is older than <paramref name="b"/>; "12.11" equals "12.11.0".</summary>
    public static int Compare(string a, string b)
    {
        var left = Parts(a);
        var right = Parts(b);
        for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
        {
            var x = i < left.Count ? left[i] : 0;
            var y = i < right.Count ? right[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    /// <summary>A text that sorts like the version (each number with 6 digits).</summary>
    public static string SortKey(string version) =>
        string.Join('.', Parts(version).Select(p => p.ToString("D6", CultureInfo.InvariantCulture)));

    private static List<long> Parts(string version) =>
        [.. (version ?? string.Empty).Split('.', '-', '_').Select(p => long.TryParse(new string([.. p.TakeWhile(char.IsDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)];
}
