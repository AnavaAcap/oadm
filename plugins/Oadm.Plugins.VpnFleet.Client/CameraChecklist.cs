using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.VpnFleet.Client;

/// <summary>What an action on many cameras does to one camera.</summary>
public enum CheckKind
{
    /// <summary>Set up: the camera has the app.</summary>
    Ready,

    /// <summary>Install: an older version is installed.</summary>
    Upgrade,

    /// <summary>Install: the app is not installed.</summary>
    Install,

    /// <summary>Install: the newest version is installed.</summary>
    UpToDate,

    /// <summary>Set up: the app is not installed (the task would refuse the camera).</summary>
    NoApp,

    /// <summary>The camera was not read yet.</summary>
    NotRead,

    /// <summary>The camera could not be read.</summary>
    Failed,

    /// <summary>The WireGuard hub: never set up as a camera.</summary>
    Hub,

    /// <summary>Install from this computer: none of the packages fits the camera.</summary>
    NoPackage,
}

/// <summary>One camera of a list: what the action does to it, and whether it is in.</summary>
public sealed partial class CameraCheck : ObservableObject
{
    public CameraCheck(Guid id, string address, string model, string installed, string note, CheckKind kind, bool isChecked)
    {
        Id = id;
        Address = address;
        Model = model;
        Installed = installed;
        Note = note;
        Kind = kind;
        IsChecked = isChecked && IsEnabled;
    }

    public Guid Id { get; }

    public string Address { get; }

    public string Model { get; }

    /// <summary>The installed version, or empty.</summary>
    public string Installed { get; }

    /// <summary>"Upgrade 1.104.6 to 1.104.7", "Tailscale installed", "App not installed. Install it first."</summary>
    public string Note { get; }

    public CheckKind Kind { get; }

    public string Label => Model.Length == 0 ? Address : $"{Address} ({Model})";

    public bool IsWarning => Kind is CheckKind.NoApp or CheckKind.Failed or CheckKind.NoPackage;

    public bool IsEnabled => Kind != CheckKind.Hub;

    /// <summary>The camera has the app of the action installed.</summary>
    public bool HasApp => Kind is CheckKind.Ready or CheckKind.Upgrade or CheckKind.UpToDate;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>Raised when <see cref="IsChecked"/> changes.</summary>
    public event EventHandler? CheckedChanged;

    partial void OnIsCheckedChanged(bool value) => CheckedChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>True when the address, model or note contains <paramref name="text"/>.</summary>
    public bool Matches(string text) =>
        Address.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Model.Contains(text, StringComparison.OrdinalIgnoreCase)
        || Note.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The cameras an action on many cameras changes: a table with a tick per camera, a search, quick ticks by what happens
/// to the cameras, and the counts the last step of the wizard shows. Built for thousands of cameras: one reset per change
/// of the shown rows, counts kept as they change.
/// </summary>
public sealed partial class CameraChecklist : ObservableObject
{
    private List<CameraCheck> _all = [];

    /// <summary>The rows the table shows (all, or those of the search).</summary>
    public RangeObservableCollection<CameraCheck> Items { get; } = [];

    /// <summary>Every camera, also those the search hides.</summary>
    public IReadOnlyList<CameraCheck> All => _all;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>"12 of 340 cameras ticked".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial int CheckedCount { get; private set; }

    public IReadOnlyList<Guid> CheckedIds => [.. _all.Where(i => i.IsChecked).Select(i => i.Id)];

    public event EventHandler? Changed;

    /// <summary>The ticked cameras by what happens to them.</summary>
    public IReadOnlyDictionary<CheckKind, int> CheckedByKind() =>
        _all.Where(i => i.IsChecked).GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>How many cameras of a kind there are, ticked or not.</summary>
    public int CountOf(CheckKind kind) => _all.Count(i => i.Kind == kind);

    public void Set(IEnumerable<CameraCheck> items)
    {
        foreach (var old in _all)
        {
            old.CheckedChanged -= OnItemChanged;
        }

        _all = [.. items];
        foreach (var item in _all)
        {
            item.CheckedChanged += OnItemChanged;
        }

        Filter();
    }

    partial void OnSearchChanged(string value) => Filter();

    /// <summary>Ticks every shown camera.</summary>
    [RelayCommand]
    private void CheckAll() => SetShown(_ => true);

    /// <summary>Unticks every shown camera.</summary>
    [RelayCommand]
    private void CheckNone() => SetShown(_ => false);

    /// <summary>Ticks exactly the shown cameras of <paramref name="kind"/>.</summary>
    public void CheckOnly(CheckKind kind) => SetShown(i => i.Kind == kind);

    private void SetShown(Func<CameraCheck, bool> value)
    {
        foreach (var item in Items.Where(i => i.IsEnabled))
        {
            item.CheckedChanged -= OnItemChanged;
            item.IsChecked = value(item);
            item.CheckedChanged += OnItemChanged;
        }

        Update();
    }

    private void Filter()
    {
        var text = Search.Trim();
        Items.ReplaceAll(text.Length == 0 ? _all : _all.Where(i => i.Matches(text)));
        Update();
    }

    private void OnItemChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        CheckedCount = _all.Count(i => i.IsChecked);
        Summary = _all.Count == 0
            ? "Choose devices on the page first."
            : $"{CheckedCount:N0} of {_all.Count:N0} cameras ticked" + (Items.Count == _all.Count ? "." : $", {Items.Count:N0} shown.");
        OnPropertyChanged(nameof(CheckedIds));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
