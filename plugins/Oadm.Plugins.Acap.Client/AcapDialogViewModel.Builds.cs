using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Plugins.Acap.Client;

/// <summary>
/// One group of selected devices (model, AXIS OS, architecture) in the package table: which file it gets and why. Shown only
/// for groups OADM cannot decide (the file is chosen here) and, when the package brings a mapping file, for every group.
/// </summary>
public sealed partial class PackageGroupRow(AcapDeviceGroup group, int count, IReadOnlyList<string> choices, bool isChoosable, string? decided, string source) : ObservableObject
{
    public AcapDeviceGroup Group { get; } = group;

    /// <summary>"P3265-V, AXIS OS 12.11.77, aarch64 (4 devices)".</summary>
    public string Label { get; } = string.Create(CultureInfo.CurrentCulture, $"{group.Label} ({count:N0} {(count == 1 ? "device" : "devices")})");

    public int DeviceCount { get; } = count;

    /// <summary>The files to choose from: the candidates of an unclear group, the decided file otherwise.</summary>
    public IReadOnlyList<string> Choices { get; } = choices;

    /// <summary>OADM cannot decide: the user chooses the file.</summary>
    public bool IsChoosable { get; } = isChoosable;

    /// <summary>"Row 2 of the mapping file", "Automatic", "Choose one".</summary>
    public string Source { get; } = source;

    /// <summary>The file this group gets; null while an unclear group has no choice.</summary>
    [ObservableProperty]
    public partial string? SelectedChoice { get; set; } = decided;

    internal Action<PackageGroupRow>? Changed { get; set; }

    partial void OnSelectedChoiceChanged(string? value) => Changed?.Invoke(this);
}

/// <summary>The build of each device group (shared <see cref="AcapBuildChooser"/>), the package table and the payload's choices.</summary>
public sealed partial class AcapDialogViewModel
{
    /// <summary>The source of a group the user decides in the table.</summary>
    public const string ChooseSource = "Choose one";

    /// <summary>The package fact when the package brings a mapping file.</summary>
    public const string MappingFact = "Mapping from the package";

    /// <summary>The file names of the packages, in the order of <see cref="Packages"/> (unique, a mapping names them).</summary>
    private IReadOnlyList<string> _names = [];

    /// <summary>The rows of the package's mapping file (<c>oadm-packages.json</c>), empty without.</summary>
    private IReadOnlyList<AcapPackageRule> _rules = [];

    /// <summary>The user's file per unclear group key.</summary>
    private readonly Dictionary<string, string> _chosen = new(StringComparer.Ordinal);

    /// <summary>The groups the package table shows (virtualized grid, replaced as a whole).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGroups))]
    public partial IReadOnlyList<PackageGroupRow> Groups { get; private set; } = [];

    /// <summary>The package table is shown: a group OADM cannot decide, or the package's mapping file decides.</summary>
    public bool ShowGroups => Groups.Count > 0;

    /// <summary>The package brings a mapping file.</summary>
    public bool HasMappingFile => _rules.Count > 0;

    /// <summary>Unclear groups without the user's choice; Install waits for them.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(InstallBlockedReason))]
    public partial int UnchosenCount { get; private set; }

    private string? _firstUnchosen;

    /// <summary>"Choose the package for P3265-V, AXIS OS 10.12.1, aarch64." or for several groups.</summary>
    private string? UnchosenReason => UnchosenCount switch
    {
        0 => null,
        1 => $"Choose the package for {_firstUnchosen}.",
        var n => string.Create(CultureInfo.CurrentCulture, $"Choose the package for {n:N0} device groups."),
    };

    private IReadOnlyList<AcapBuild> Builds => AcapCompatibility.Builds(_packages, _names);

    /// <summary>The user's choices as the payload's mapping (file names until the upload gives ids).</summary>
    private List<AcapPackageMapping> ChosenMapping() =>
        [.. Groups.Where(g => g.IsChoosable && g.SelectedChoice is not null).Select(g => AcapPackageMapping.For(g.Group, g.SelectedChoice!))];

    /// <summary>
    /// Groups every selected device by model, AXIS OS and architecture (the dialog's read for checked devices, the cached facts
    /// for the others) in one pass, chooses the build of each group, refreshes the table and the checked rows. O(n).
    /// </summary>
    private void ApplyChoices()
    {
        var builds = Builds;
        var rows = Compatibility;
        if (builds.Count <= 1 || rows.Count == 0)
        {
            Groups = [];
            UnchosenCount = 0;
            ApplyPreviews(builds, rows);
            return;
        }

        var byKey = new Dictionary<string, (AcapDeviceGroup Group, int Count, AcapBuildChoice Auto)>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var group = rows[i].State is { } state ? AcapDeviceGroup.Of(state.Device, rows[i].Device.Device.Model) : AcapDeviceGroup.Of(rows[i].Device.Device);
            var key = group.Key;
            if (byKey.TryGetValue(key, out var known))
            {
                byKey[key] = known with { Count = known.Count + 1 };
            }
            else
            {
                // The SDK facts of the group's first row when it was read (one model and AXIS OS report the same); the rows check their own.
                byKey[key] = (group, 1, AcapBuildChooser.Choose(builds, group, _rules, device: rows[i].State?.Device));
            }
        }

        var table = new List<PackageGroupRow>();
        foreach (var (key, (group, count, auto)) in byKey.OrderByDescending(p => p.Value.Auto.IsUnclear).ThenByDescending(p => p.Value.Count).ThenBy(p => p.Value.Group.Label, StringComparer.Ordinal))
        {
            PackageGroupRow row;
            if (auto.IsUnclear)
            {
                var names = auto.Candidates.Select(c => builds[c].Name).ToList();
                var chosen = _chosen.TryGetValue(key, out var file) && names.Contains(file, StringComparer.Ordinal) ? file : null;
                row = new PackageGroupRow(group, count, names, isChoosable: true, chosen, ChooseSource);
            }
            else if (_rules.Count > 0 && group.IsKnown)
            {
                row = new PackageGroupRow(group, count, auto.IsChosen ? [builds[auto.Index].Name] : [], isChoosable: false, auto.IsChosen ? builds[auto.Index].Name : null,
                    auto.IsChosen ? auto.DecidedBy : "No package");
            }
            else
            {
                continue;
            }

            row.Changed = OnGroupChosen;
            table.Add(row);
        }

        Groups = table;
        CountUnchosen();
        ApplyPreviews(builds, rows);
    }

    private void OnGroupChosen(PackageGroupRow row)
    {
        if (row.SelectedChoice is { } file)
        {
            _chosen[row.Group.Key] = file;
        }

        CountUnchosen();
        var builds = Builds;
        var rows = Compatibility;
        ApplyPreviews(builds, rows);
    }

    private void CountUnchosen()
    {
        var open = Groups.Where(g => g.IsChoosable && g.SelectedChoice is null).ToList();
        _firstUnchosen = open.FirstOrDefault()?.Group.Label;
        UnchosenCount = open.Count;
        OnPropertyChanged(nameof(InstallBlockedReason));
    }

    /// <summary>The preview of every checked device with its group's build (the user's choice included) and the counts.</summary>
    private void ApplyPreviews(IReadOnlyList<AcapBuild> builds, IReadOnlyList<DeviceCompatibilityRow> rows)
    {
        var mapping = ChosenMapping();
        var compatible = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.State is not { } state || builds.Count == 0)
            {
                continue;
            }

            var choice = AcapBuildChooser.Choose(builds, AcapDeviceGroup.Of(state.Device, row.Device.Device.Model), _rules, mapping, state.Device);
            var preview = AcapCompatibility.Preview(builds, choice, state.Device, state.Applications, AllowDowngrade);
            row.PackageArchitecture = preview.PackageArchitecture;
            row.IsOk = preview.IsOk;
            row.Kind = preview.Kind;
            row.IsWarning = preview.IsWarning;
            row.IsError = preview.IsError;
            row.Result = preview.Result;
            row.Verdict = preview.Verdict;
            row.Details = preview.Details;
            compatible += preview.IsOk ? 1 : 0;
        }

        CompatibleCount = compatible;
        OnPropertyChanged(nameof(CompatibilitySummary));
    }

    /// <summary>The mapping rows and the choices with the upload's file ids in place of the file names.</summary>
    private (IReadOnlyList<AcapPackageRule>? Rules, IReadOnlyList<AcapPackageMapping>? Mapping) PayloadChoices(List<AcapPackageFile> files)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count && i < _names.Count; i++)
        {
            ids[_names[i]] = files[i].FileId;
        }

        var rules = _rules.Count == 0 ? null : _rules.Select(r => r with { File = ids.GetValueOrDefault(r.File, r.File) }).ToList();
        var mapping = ChosenMapping().Select(m => m with { File = ids.GetValueOrDefault(m.File, m.File) }).ToList();
        return (rules, mapping.Count == 0 ? null : mapping);
    }
}
