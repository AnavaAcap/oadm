using System.Text.RegularExpressions;

using Oadm.Client.Devices;

using ContractStatus = Oadm.Contracts.V1.DeviceStatus;

namespace Oadm.Client.Assistant;

/// <summary>A device named in an answer: the text from <see cref="Start"/> with <see cref="Length"/> characters.</summary>
public readonly record struct AssistantDeviceMatch(int Start, int Length, string DeviceId);

/// <summary>
/// What the Assistant panel knows of the device store without a scan per change (docs/spec/assistant.md "The panel"): the
/// counts of the empty panel's chips (devices, offline, with vulnerabilities) and the lookup that turns device addresses,
/// host names and serial numbers in an answer into links. Kept per change of the store (O(changed devices)); a reset
/// (snapshot) or a new vulnerability list counts once (O(n)). While the panel is closed nothing is kept up to date: it
/// counts again when it opens (<see cref="IsActive"/>). Lives on the UI thread.
/// </summary>
public sealed partial class AssistantDeviceIndex
{
    private readonly DeviceStore _store;
    private readonly Dictionary<string, (bool Offline, bool Vulnerable, string[] Keys)> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty = true;

    public AssistantDeviceIndex(DeviceStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        store.Changed += (_, e) =>
        {
            if (!IsActive || _dirty || e.IsReset)
            {
                _dirty = true;
                Rebuild();
                return;
            }

            foreach (var id in e.DeviceIds)
            {
                Update(id, store.Find(id));
            }

            Changed?.Invoke(this, EventArgs.Empty);
        };
        store.Vulnerabilities.Changed += (_, _) =>
        {
            _dirty = true;
            Rebuild();
        };
    }

    /// <summary>The panel is open: the counts and the lookup follow the store.</summary>
    public bool IsActive
    {
        get;
        set
        {
            field = value;
            if (value)
            {
                Rebuild();
            }
        }
    }

    public int Total { get; private set; }

    /// <summary>Unreachable, or at a site that is offline.</summary>
    public int Offline { get; private set; }

    /// <summary>With at least one certain vulnerability (the Vulnerabilities column without "possible" ones).</summary>
    public int Vulnerable { get; private set; }

    /// <summary>The counts or the lookup changed (one event per store change).</summary>
    public event EventHandler? Changed;

    private void Rebuild()
    {
        if (!IsActive || !_dirty)
        {
            return;
        }

        _dirty = false;
        _devices.Clear();
        _byKey.Clear();
        Total = Offline = Vulnerable = 0;
        foreach (var row in _store.Devices)
        {
            Update(row.Id, row);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Update(string id, DeviceRowViewModel? row)
    {
        if (_devices.Remove(id, out var old))
        {
            Total--;
            Offline -= old.Offline ? 1 : 0;
            Vulnerable -= old.Vulnerable ? 1 : 0;
            foreach (var key in old.Keys)
            {
                if (_byKey.TryGetValue(key, out var ids) && ids.Remove(id) && ids.Count == 0)
                {
                    _byKey.Remove(key);
                }
            }
        }

        if (row is null)
        {
            return;
        }

        bool offline = row.ContractStatus is ContractStatus.Unreachable or ContractStatus.SiteOffline;
        bool vulnerable = row.Vulnerabilities.Certain > 0;
        string[] keys = [.. new[] { row.Address, row.HostName, row.Serial }.Where(k => !string.IsNullOrWhiteSpace(k) && k!.Length >= 3).Select(k => k!).Distinct(StringComparer.OrdinalIgnoreCase)];
        _devices[id] = (offline, vulnerable, keys);
        Total++;
        Offline += offline ? 1 : 0;
        Vulnerable += vulnerable ? 1 : 0;
        foreach (var key in keys)
        {
            if (!_byKey.TryGetValue(key, out var ids))
            {
                _byKey[key] = ids = [];
            }

            ids.Add(id);
        }
    }

    /// <summary>The device a key names (address, host name or serial), null when none or more than one.</summary>
    public string? Lookup(string key) => _byKey.TryGetValue(key, out var ids) && ids.Count == 1 ? ids[0] : null;

    /// <summary>
    /// The devices an answer names: each word (letters, digits, dots, dashes, underscores, colons) that is the address,
    /// host name or serial number of exactly one device. O(length of the text), independent of the device count.
    /// </summary>
    public IReadOnlyList<AssistantDeviceMatch> Find(string? text)
    {
        if (string.IsNullOrEmpty(text) || _byKey.Count == 0)
        {
            return [];
        }

        List<AssistantDeviceMatch>? found = null;
        foreach (Match word in Word().Matches(text))
        {
            var value = word.Value.TrimEnd('.', ':', '-');
            if (value.Length >= 3 && Lookup(value) is { } id)
            {
                (found ??= []).Add(new AssistantDeviceMatch(word.Index, value.Length, id));
            }
        }

        return found ?? (IReadOnlyList<AssistantDeviceMatch>)[];
    }

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}._:\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Word();
}
