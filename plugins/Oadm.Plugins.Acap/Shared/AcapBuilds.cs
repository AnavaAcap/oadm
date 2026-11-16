using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Oadm.Plugins.Acap;

/// <summary>
/// One build of an install: a package file with its key (the upload's file id, or the .eap name inside an uploaded ZIP; in
/// the dialog before the upload: the file name), the name shown to the user and what the package says about itself.
/// </summary>
public sealed record AcapBuild(string Key, string Name, EapManifest Package);

/// <summary>
/// A group of devices that get the same build: model, AXIS OS and architecture (the SoC and the short name only help the
/// rows of a mapping file). <see cref="Key"/> compares the model without "AXIS ", the AXIS OS as written and the architecture
/// normalized.
/// </summary>
public sealed record AcapDeviceGroup(string? Model, string? FirmwareVersion, string? Architecture, string? Soc = null, string? ShortName = null)
{
    /// <summary>"P3265-V, AXIS OS 12.11.77, aarch64".</summary>
    public string Label => string.Join(", ",
        string.IsNullOrWhiteSpace(Model) ? "Unknown model" : Model.Trim(),
        string.IsNullOrWhiteSpace(FirmwareVersion) ? "AXIS OS unknown" : "AXIS OS " + FirmwareVersion.Trim(),
        string.IsNullOrWhiteSpace(Architecture) ? "architecture unknown" : Architecture.Trim());

    public string Key => string.Join('|', AcapPackageRules.ModelKey(Model), (FirmwareVersion ?? string.Empty).Trim().ToLowerInvariant(),
        string.IsNullOrWhiteSpace(Architecture) ? string.Empty : AcapCompatibility.NormalizeArchitecture(Architecture));

    /// <summary>Both the architecture and the AXIS OS are known (else only the device's own read can decide).</summary>
    public bool IsKnown => !string.IsNullOrWhiteSpace(Architecture) && !string.IsNullOrWhiteSpace(FirmwareVersion);

    /// <summary>The group of a device's cached facts.</summary>
    public static AcapDeviceGroup Of(Oadm.Sdk.Devices.IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new(device.Model, device.FirmwareVersion, device.Architecture, device.Soc);
    }

    /// <summary>The group of a fresh read of the device (the task, the dialog's query).</summary>
    public static AcapDeviceGroup Of(AcapDeviceFacts facts, string? cachedModel = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return new(facts.Model ?? cachedModel, facts.FirmwareVersion, facts.Architecture, facts.Soc, facts.ShortName);
    }
}

/// <summary>One row of a mapping (<c>oadm-packages.json</c> in a vendor's ZIP, or the payload's <c>rules</c>).</summary>
[Description("One row of a package mapping: the devices it matches and the package they get.")]
public sealed record AcapPackageRule(
    [property: Description("The package: an uploaded file id of files, or the .eap name inside the ZIP."), MinLength(1), MaxLength(260)] string File,
    [property: Description("Optional: the models it matches (P3265-LVE; \"AXIS \" is ignored)."), MaxLength(AcapPackageRules.MaxValues)] IReadOnlyList<string>? Models = null,
    [property: Description("Optional: the systems on chip it matches (CV25, ARTPEC-8 or A8)."), MaxLength(AcapPackageRules.MaxValues)] IReadOnlyList<string>? Soc = null,
    [property: Description("Optional: the architecture it matches (aarch64)."), MaxLength(32)] string? Architecture = null,
    [property: Description("Optional: the AXIS OS versions it matches, bounds inclusive at their own precision (max 10.12 = any 10.12.x).")] OsVersionRange? AxisOs = null)
{
    /// <summary>How specific the row is: models first, then the SoC, then the architecture and AXIS OS.</summary>
    internal (int Models, int Soc, int Others) Specificity =>
        (Models is { Count: > 0 } ? 1 : 0, Soc is { Count: > 0 } ? 1 : 0, (string.IsNullOrWhiteSpace(Architecture) ? 0 : 1) + (AxisOs is null ? 0 : 1));

    /// <summary>The row matches the group: every key it names fits (an unknown fact never matches a key).</summary>
    public bool Matches(AcapDeviceGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (Models is { Count: > 0 } models)
        {
            var model = AcapPackageRules.ModelKey(group.Model);
            var shortName = AcapPackageRules.ModelKey(group.ShortName);
            if (!models.Any(m => AcapPackageRules.ModelKey(m) is { Length: > 0 } key && (key == model || key == shortName)))
            {
                return false;
            }
        }

        if (Soc is { Count: > 0 } socs)
        {
            var soc = AcapPackageRules.SocKey(group.Soc);
            if (soc.Length == 0 || !socs.Any(s => AcapPackageRules.SocKey(s) == soc))
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(Architecture)
            && (string.IsNullOrWhiteSpace(group.Architecture) || AcapCompatibility.NormalizeArchitecture(Architecture) != AcapCompatibility.NormalizeArchitecture(group.Architecture)))
        {
            return false;
        }

        return AxisOs is null || (!string.IsNullOrWhiteSpace(group.FirmwareVersion) && AcapCompatibility.IsInRange(group.FirmwareVersion, AxisOs));
    }
}

/// <summary>What a mapping gives a device that none of its rows matches (<c>"unmatched"</c> of <c>oadm-packages.json</c>).</summary>
[Description("What a device that no mapping row matches gets.")]
public enum AcapUnmatched
{
    /// <summary>The automatic pick of the manifests: architecture and AXIS OS (the default).</summary>
    Automatic = 0,

    /// <summary>No package: only the devices a row matches get one.</summary>
    Skip = 1,
}

/// <summary>A mapping (<c>oadm-packages.json</c>): its rows and what a device that no row matches gets.</summary>
public sealed record AcapPackageMap(IReadOnlyList<AcapPackageRule> Rows, AcapUnmatched Unmatched = AcapUnmatched.Automatic)
{
    /// <summary>No mapping: no rows, the automatic pick for every device.</summary>
    public static AcapPackageMap None { get; } = new([]);
}

/// <summary>The build a person (the dialog's table) or an agent chose for a group OADM could not decide.</summary>
[Description("The package of one group of devices OADM cannot choose by itself (the preview names them).")]
public sealed record AcapPackageMapping(
    [property: Description("The AXIS OS version of the group (12.11.77)."), MinLength(1), MaxLength(64)] string AxisOs,
    [property: Description("The architecture of the group (aarch64)."), MinLength(1), MaxLength(32)] string Architecture,
    [property: Description("The package: an uploaded file id of files, or the .eap name inside the ZIP."), MinLength(1), MaxLength(260)] string File,
    [property: Description("Optional: the model of the group (P3265-V); left out, every model of this AXIS OS and architecture."), MaxLength(64)] string? Model = null)
{
    /// <summary>The entry is for this group.</summary>
    public bool Matches(AcapDeviceGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(group.Architecture) || string.IsNullOrWhiteSpace(group.FirmwareVersion)
            || AcapCompatibility.NormalizeArchitecture(Architecture) != AcapCompatibility.NormalizeArchitecture(group.Architecture)
            || !string.Equals(AxisOs.Trim(), group.FirmwareVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var model = AcapPackageRules.ModelKey(Model);
        return model.Length == 0 || model == AcapPackageRules.ModelKey(group.Model) || model == AcapPackageRules.ModelKey(group.ShortName);
    }

    /// <summary>The entry for <paramref name="group"/>.</summary>
    public static AcapPackageMapping For(AcapDeviceGroup group, string file)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new(group.FirmwareVersion?.Trim() ?? string.Empty, group.Architecture?.Trim() ?? string.Empty, file, string.IsNullOrWhiteSpace(group.Model) ? null : group.Model.Trim());
    }
}

/// <summary>How a build was found for a group.</summary>
public enum AcapPick
{
    /// <summary>One build fits (<see cref="AcapBuildChoice.Index"/>).</summary>
    Chosen = 0,

    /// <summary>No build fits (<see cref="AcapBuildChoice.Reason"/>).</summary>
    None = 1,

    /// <summary>Several fit equally (<see cref="AcapBuildChoice.Candidates"/>): a person or an agent chooses.</summary>
    Unclear = 2,
}

/// <summary>The build of one group (<see cref="AcapBuildChooser.Choose"/>).</summary>
/// <param name="Index">The chosen build, -1 for none.</param>
/// <param name="DecidedBy">"Automatic", "Row 2 of the mapping file", "Chosen".</param>
public sealed record AcapBuildChoice(int Index, AcapPick Pick, IReadOnlyList<int> Candidates, string? Reason, string DecidedBy)
{
    public const string Automatic = "Automatic";
    public const string ChosenByPerson = "Chosen";

    /// <summary>A mapping with <c>"unmatched": "skip"</c> and no row for the group.</summary>
    public const string MappingFile = "The mapping file";

    /// <summary>The reason of a group no row matches when the mapping says <c>"unmatched": "skip"</c>.</summary>
    public const string NoRowMatches = "No row of the mapping file matches this device.";

    public bool IsChosen => Pick == AcapPick.Chosen;

    public bool IsUnclear => Pick == AcapPick.Unclear;
}

/// <summary>
/// The pick of a build per device group (plugin README "Several packages"), one pure function for the dialog's preview, the
/// task's plan (agents) and the task's run with a fresh read of the device: 1) the rows of a mapping file that match (the most
/// specific wins; a tie is unclear), also for a single package; with <see cref="AcapUnmatched.Skip"/> a group no row matches
/// gets no package; 2) automatic: the architecture fits and the AXIS OS is inside the build's range (without a range: the
/// manifest schema's minimum); among them the newest version, then the range that lies inside the others'; a build for any
/// architecture only when no native one fits; a single package without rows is always used (its own checks decide);
/// 3) the payload's choice decides only a group that 1) and 2) leave unclear, never one they decided or left without a
/// package. A choice never overrides safety: the chosen build must fit the device's architecture and AXIS OS. With the device's
/// own read (the dialog, the Applications page, the task) every candidate build must also fit its SDK
/// (<see cref="AcapCompatibility.CheckSdk"/>); the cached data of a plan has no SDK facts, so the task checks them.
/// </summary>
public static class AcapBuildChooser
{
    /// <summary>"Several packages fit: a.eap, b.eap."</summary>
    public static string SeveralFit(IEnumerable<string> names) => "Several packages fit: " + string.Join(", ", names) + ".";

    /// <param name="device">The device's own read for the SDK rules; null: not checked here (<see cref="AcapCompatibility.Check"/> does).</param>
    public static AcapBuildChoice Choose(IReadOnlyList<AcapBuild> builds, AcapDeviceGroup group, IReadOnlyList<AcapPackageRule>? rules = null,
        IReadOnlyList<AcapPackageMapping>? mapping = null, AcapUnmatched unmatched = AcapUnmatched.Automatic, AcapDeviceFacts? device = null)
    {
        ArgumentNullException.ThrowIfNull(builds);
        ArgumentNullException.ThrowIfNull(group);
        if (builds.Count == 0)
        {
            return new(-1, AcapPick.None, [], "No package.", AcapBuildChoice.Automatic);
        }

        // A single package without a row for the group is always used: its own checks say whether it fits.
        var choice = ByRows(builds, group, rules, unmatched, device)
            ?? (builds.Count == 1 ? new(0, AcapPick.Chosen, [0], null, AcapBuildChoice.Automatic) : Automatic(builds, group, device));
        if (!choice.IsUnclear || mapping?.FirstOrDefault(m => m.Matches(group)) is not { } mapped)
        {
            return choice;
        }

        var index = IndexOf(builds, mapped.File);
        return index < 0
            ? new(-1, AcapPick.None, [], $"The chosen package {mapped.File} is not part of this install.", AcapBuildChoice.ChosenByPerson)
            : Fitting(builds, index, group, AcapBuildChoice.ChosenByPerson, string.Empty, device);
    }

    /// <summary>The choice of the mapping's rows; null when no row matches and the mapping leaves the group to the automatic pick.</summary>
    private static AcapBuildChoice? ByRows(IReadOnlyList<AcapBuild> builds, AcapDeviceGroup group, IReadOnlyList<AcapPackageRule>? rules, AcapUnmatched unmatched,
        AcapDeviceFacts? device)
    {
        var matching = new List<int>();
        for (var i = 0; rules is not null && i < rules.Count; i++)
        {
            if (rules[i].Matches(group))
            {
                matching.Add(i);
            }
        }

        if (matching.Count == 0)
        {
            return unmatched == AcapUnmatched.Skip ? new(-1, AcapPick.None, [], AcapBuildChoice.NoRowMatches, AcapBuildChoice.MappingFile) : null;
        }

        var best = matching.Max(i => rules![i].Specificity);
        var top = matching.Where(i => rules![i].Specificity == best).ToList();
        var files = top.Select(i => IndexOf(builds, rules![i].File)).Where(i => i >= 0).Distinct().ToList();
        var rows = RowsText(top);
        if (files.Count > 1)
        {
            return new(-1, AcapPick.Unclear, files, $"{rows} of the mapping file fit equally: {string.Join(", ", files.Select(i => builds[i].Name))}.", rows + " of the mapping file");
        }

        return files.Count == 0
            ? new(-1, AcapPick.None, [], $"{rows} of the mapping file names a package that is not part of this install.", rows + " of the mapping file")
            : Fitting(builds, files[0], group, rows + " of the mapping file", $" from {rows.ToLowerInvariant()} of the mapping file", device);
    }

    /// <summary>
    /// Why the package does not fit the group, null when it does: architecture and AXIS OS, with <paramref name="device"/> also
    /// the SDK (<see cref="AcapCompatibility.CheckSdk"/>); the rest is <see cref="AcapCompatibility.Check"/>.
    /// </summary>
    public static string? Misfit(EapManifest package, AcapDeviceGroup group, AcapDeviceFacts? device = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(group);
        if (!AcapCompatibility.IsArchitectureIndependent(package.Architecture))
        {
            if (string.IsNullOrWhiteSpace(group.Architecture))
            {
                return "the architecture of the device is unknown";
            }

            if (AcapCompatibility.NormalizeArchitecture(package.Architecture!) != AcapCompatibility.NormalizeArchitecture(group.Architecture))
            {
                return $"it is for {package.Architecture}";
            }
        }

        return OsMisfit(package, group.FirmwareVersion) ?? SdkMisfit(package, device)?.Fragment;
    }

    private static (string Sentence, string Fragment)? SdkMisfit(EapManifest package, AcapDeviceFacts? device) =>
        device is null ? null : AcapCompatibility.CheckSdk(package, device);

    private static string? OsMisfit(EapManifest package, string? firmware)
    {
        var known = !string.IsNullOrWhiteSpace(firmware) && AcapCompatibility.HasNumericVersion(firmware);
        if (package.CompatibleOsVersions.Count > 0)
        {
            return !known ? "the AXIS OS version of the device is unknown"
                : package.CompatibleOsVersions.Any(r => AcapCompatibility.IsInRange(firmware!, r)) ? null
                : $"it needs AXIS OS {string.Join(" or ", package.CompatibleOsVersions)}";
        }

        var min = AcapCompatibility.MinimumOsForSchema(package.SchemaVersion, out _);
        return min is null ? null
            : !known ? "the AXIS OS version of the device is unknown"
            : AcapCompatibility.IsInRange(firmware!, new OsVersionRange(min, null)) ? null
            : $"it needs AXIS OS {min} or later";
    }

    private static AcapBuildChoice Fitting(IReadOnlyList<AcapBuild> builds, int index, AcapDeviceGroup group, string decidedBy, string from, AcapDeviceFacts? device) =>
        Misfit(builds[index].Package, group, device) is { } why
            ? new(-1, AcapPick.None, [], $"{builds[index].Name}{from} does not fit: {why}.", decidedBy)
            : new(index, AcapPick.Chosen, [index], null, decidedBy);

    private static AcapBuildChoice Automatic(IReadOnlyList<AcapBuild> builds, AcapDeviceGroup group, AcapDeviceFacts? device)
    {
        var native = new List<int>();
        var any = new List<int>();
        for (var i = 0; i < builds.Count; i++)
        {
            var arch = builds[i].Package.Architecture;
            if (AcapCompatibility.IsArchitectureIndependent(arch))
            {
                any.Add(i);
            }
            else if (!string.IsNullOrWhiteSpace(group.Architecture) && AcapCompatibility.NormalizeArchitecture(arch!) == AcapCompatibility.NormalizeArchitecture(group.Architecture))
            {
                native.Add(i);
            }
        }

        // A package for any architecture is the last resort.
        string? sdkReason = null;
        foreach (var tier in new[] { native, any })
        {
            var osFit = tier.Where(i => OsMisfit(builds[i].Package, group.FirmwareVersion) is null).ToList();
            var fit = osFit.Where(i => SdkMisfit(builds[i].Package, device) is null).ToList();
            if (fit.Count > 0)
            {
                return Best(builds, fit, group.FirmwareVersion);
            }

            if (sdkReason is null && osFit.Count > 0)
            {
                sdkReason = "No package: " + SdkMisfit(builds[osFit[0]].Package, device)!.Value.Sentence;
            }
        }

        var reason = sdkReason is not null ? sdkReason
            : native.Count == 0 && any.Count == 0 ? AcapCompatibility.NoPackageFor(group.Architecture)
            : string.IsNullOrWhiteSpace(group.FirmwareVersion) || !AcapCompatibility.HasNumericVersion(group.FirmwareVersion) ? "No package for this device: its AXIS OS version is unknown."
            : $"No package for AXIS OS {group.FirmwareVersion.Trim()}.";
        return new(-1, AcapPick.None, [], reason, AcapBuildChoice.Automatic);
    }

    /// <summary>The newest version; of equal versions the one whose range lies inside the others'; else unclear.</summary>
    private static AcapBuildChoice Best(IReadOnlyList<AcapBuild> builds, List<int> fit, string? firmware)
    {
        var newest = fit.Select(i => builds[i].Package.Version).Max(Comparer<string>.Create(AcapCompatibility.CompareVersions))!;
        var top = fit.Where(i => AcapCompatibility.CompareVersions(builds[i].Package.Version, newest) == 0).ToList();
        if (top.Count == 1)
        {
            return new(top[0], AcapPick.Chosen, top, null, AcapBuildChoice.Automatic);
        }

        // A build without a range competing with another cannot be decided.
        if (top.Any(i => builds[i].Package.CompatibleOsVersions.Count == 0) || string.IsNullOrWhiteSpace(firmware))
        {
            return new(-1, AcapPick.Unclear, top, SeveralFit(top.Select(i => builds[i].Name)), AcapBuildChoice.Automatic);
        }

        var ranges = top.ToDictionary(i => i, i => Narrowest(builds[i].Package.CompatibleOsVersions.Where(r => AcapCompatibility.IsInRange(firmware, r))));
        var minimal = top.Where(i => !top.Any(o => o != i && StrictlyInside(ranges[o], ranges[i]))).ToList();
        var winners = minimal.Where(i => minimal.All(o => o == i || StrictlyInside(ranges[i], ranges[o]))).ToList();
        return winners.Count == 1
            ? new(winners[0], AcapPick.Chosen, [winners[0]], null, AcapBuildChoice.Automatic)
            : new(-1, AcapPick.Unclear, minimal, SeveralFit(minimal.Select(i => builds[i].Name)), AcapBuildChoice.Automatic);
    }

    private static (long[] Low, long[] High) Narrowest(IEnumerable<OsVersionRange> ranges) =>
        ranges.Select(r => (Low: Bound(r.Min, low: true), High: Bound(r.Max, low: false)))
            .OrderByDescending(r => r.Low, BoundComparer).ThenBy(r => r.High, BoundComparer).First();

    /// <summary><paramref name="inner"/> lies inside <paramref name="outer"/> and is not the same range.</summary>
    private static bool StrictlyInside((long[] Low, long[] High) inner, (long[] Low, long[] High) outer)
    {
        var low = BoundComparer.Compare(inner.Low, outer.Low);
        var high = BoundComparer.Compare(inner.High, outer.High);
        return low >= 0 && high <= 0 && (low > 0 || high < 0);
    }

    private const int BoundLength = 4;

    private static readonly Comparer<long[]> BoundComparer = Comparer<long[]>.Create((a, b) =>
    {
        for (var i = 0; i < BoundLength; i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return 0;
    });

    /// <summary>A bound at its own precision: a lower one filled with 0, an upper one with the largest value ("12" = any 12.x).</summary>
    private static long[] Bound(string? version, bool low)
    {
        var result = new long[BoundLength];
        var parts = AcapCompatibility.NumericParts(version);
        if (parts.Count == 0)
        {
            Array.Fill(result, low ? long.MinValue : long.MaxValue);
            return result;
        }

        for (var i = 0; i < BoundLength; i++)
        {
            result[i] = i < parts.Count ? parts[i] : low ? 0 : long.MaxValue;
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<AcapBuild> builds, string key)
    {
        for (var i = 0; i < builds.Count; i++)
        {
            if (string.Equals(builds[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < builds.Count; i++)
        {
            if (string.Equals(builds[i].Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>"Row 2", "Rows 2 and 5", "Rows 1, 2 and 5" (one-based).</summary>
    private static string RowsText(List<int> rows)
    {
        var numbers = rows.Select(r => (r + 1).ToString(CultureInfo.InvariantCulture)).ToList();
        return numbers.Count == 1 ? "Row " + numbers[0] : "Rows " + string.Join(", ", numbers[..^1]) + " and " + numbers[^1];
    }
}

/// <summary>
/// The vendor's mapping file <c>oadm-packages.json</c> (OADM's own format; Axis has none): strict JSON (no comments, no
/// trailing commas), <c>{"unmatched": "automatic" | "skip", "packages": [{"file", "models", "soc", "architecture", "axisOs":
/// {"min", "max"}}]}</c>, only <c>file</c> required and naming an .eap of the same pick, at most <see cref="MaxBytes"/> and
/// <see cref="MaxRows"/>. A key that starts with <c>$</c> or <c>x-</c> (at the top level and in a row) is a note and ignored;
/// any other unknown key refuses the file, so an older OADM never ignores a rule it does not know.
/// </summary>
public static class AcapPackageRules
{
    public const string FileName = "oadm-packages.json";

    public const int MaxBytes = 64 * 1024;

    public const int MaxRows = 500;

    /// <summary>At most this many models or SoC names in one row.</summary>
    public const int MaxValues = 200;

    private static readonly HashSet<string> RowKeys = new(StringComparer.Ordinal) { "file", "models", "soc", "architecture", "axisOs" };

    /// <summary>"P3265-LVE" for "AXIS P3265-LVE", " p3265-lve "; empty for none.</summary>
    public static string ModelKey(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var text = model.Trim();
        if (text.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].TrimStart();
        }

        return text.ToUpperInvariant();
    }

    /// <summary>
    /// The SoC as the SoC column shows it (<c>DeviceTexts.Soc</c>): "Axis Artpec-8", "ARTPEC-8" and "A8" are "A8", "Ambarella
    /// CV25" and "CV25" are "CV25"; upper case, empty for none.
    /// </summary>
    public static string SocKey(string? soc)
    {
        if (string.IsNullOrWhiteSpace(soc))
        {
            return string.Empty;
        }

        var text = soc.Trim();
        foreach (var brand in new[] { "Axis ", "Ambarella " })
        {
            if (text.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            {
                text = text[brand.Length..].TrimStart();
            }
        }

        if (text.StartsWith("Artpec", StringComparison.OrdinalIgnoreCase))
        {
            text = "A" + text[6..].TrimStart('-', ' ');
        }

        return text.ToUpperInvariant();
    }

    /// <summary>A key of the mapping file that is a note, not a rule: it starts with <c>$</c> or <c>x-</c> ("$schema", "x-vendor").</summary>
    public static bool IsAnnotation(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith('$') || key.StartsWith("x-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Why a row is not valid, null when it is: the rules of the mapping file for a row read from it or sent as the payload's
    /// <c>rules</c> ("row 2: \"axisOs.max\" must be a version such as 10.12"). <paramref name="at"/> names the row.
    /// </summary>
    public static string? RowProblem(AcapPackageRule? row, string at)
    {
        ArgumentNullException.ThrowIfNull(at);
        if (row is null)
        {
            return at + " is not an object";
        }

        if (!IsText(row.File))
        {
            return $"{at}: \"file\" must be a text";
        }

        if (row.Models is { } models && (models.Count is 0 or > MaxValues || !models.All(IsText)))
        {
            return $"{at}: \"models\" must be a list of texts";
        }

        if (row.Soc is { } soc && (soc.Count is 0 or > MaxValues || !soc.All(IsText)))
        {
            return $"{at}: \"soc\" must be a list of texts";
        }

        if (row.Architecture is { } architecture && !IsText(architecture))
        {
            return $"{at}: \"architecture\" must be a text";
        }

        if (row.AxisOs is not { } os)
        {
            return null;
        }

        if (os.Min is null && os.Max is null)
        {
            return $"{at}: \"axisOs\" needs \"min\" or \"max\"";
        }

        foreach (var (name, bound) in new[] { ("min", os.Min), ("max", os.Max) })
        {
            if (bound is not null && (!IsText(bound) || !AcapCompatibility.HasNumericVersion(bound)))
            {
                return $"{at}: \"axisOs.{name}\" must be a version such as 10.12";
            }
        }

        return AcapCompatibility.IsInverted(os) ? $"{at}: \"axisOs\" has a \"min\" above its \"max\"" : null;
    }

    private static bool IsText(string? text) => text is { Length: > 0 and <= 260 } && !string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Reads the mapping file. <paramref name="files"/> are the .eap names of the pick (a row's <c>file</c> must be one of
    /// them, upper and lower case aside). Throws <see cref="InvalidEapException"/> with one plain sentence.
    /// </summary>
    public static AcapPackageMap Parse(ReadOnlySpan<byte> json, IReadOnlyCollection<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (json.Length > MaxBytes)
        {
            throw Refused("is larger than 64 KB");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw Refused("is not valid JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Refused("must be an object with \"packages\"");
            }

            JsonElement packages = default;
            var unmatched = AcapUnmatched.Automatic;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "packages":
                        packages = property.Value;
                        break;
                    case "unmatched":
                        unmatched = property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value
                            ? value switch
                            {
                                "automatic" => AcapUnmatched.Automatic,
                                "skip" => AcapUnmatched.Skip,
                                _ => throw Refused("has \"unmatched\" that is neither \"automatic\" nor \"skip\""),
                            }
                            : throw Refused("has \"unmatched\" that is neither \"automatic\" nor \"skip\"");
                        break;
                    case var name when IsAnnotation(name):
                        break;
                    default:
                        throw Refused($"has the unknown key \"{Shorten(property.Name)}\"");
                }
            }

            if (packages.ValueKind != JsonValueKind.Array)
            {
                throw Refused("needs a list \"packages\"");
            }

            if (packages.GetArrayLength() > MaxRows)
            {
                throw Refused(string.Create(CultureInfo.InvariantCulture, $"has more than {MaxRows} rows"));
            }

            var names = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            var rows = new List<AcapPackageRule>(packages.GetArrayLength());
            var number = 0;
            foreach (var row in packages.EnumerateArray())
            {
                number++;
                rows.Add(ParseRow(row, number, names));
            }

            return new AcapPackageMap(rows, unmatched);
        }
    }

    private static AcapPackageRule ParseRow(JsonElement row, int number, HashSet<string> names)
    {
        var at = string.Create(CultureInfo.InvariantCulture, $"row {number}");
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw Refused($"{at} is not an object");
        }

        string? file = null;
        string? architecture = null;
        IReadOnlyList<string>? models = null;
        IReadOnlyList<string>? soc = null;
        OsVersionRange? os = null;
        foreach (var property in row.EnumerateObject())
        {
            if (IsAnnotation(property.Name))
            {
                continue;
            }

            if (!RowKeys.Contains(property.Name))
            {
                throw Refused($"{at} has the unknown key \"{Shorten(property.Name)}\"");
            }

            switch (property.Name)
            {
                case "file":
                    file = Text(property.Value, at, "file");
                    break;
                case "architecture":
                    architecture = Text(property.Value, at, "architecture");
                    break;
                case "models":
                    models = Texts(property.Value, at, "models");
                    break;
                case "soc":
                    soc = Texts(property.Value, at, "soc");
                    break;
                default:
                    os = Range(property.Value, at);
                    break;
            }
        }

        if (file is null)
        {
            throw Refused($"{at} has no \"file\"");
        }

        if (!names.Contains(file))
        {
            throw Refused($"{at} names {Shorten(file)}, which is not among the .eap files");
        }

        var rule = new AcapPackageRule(file, models, soc, architecture, os);
        return RowProblem(rule, at) is { } problem ? throw Refused(problem) : rule;
    }

    private static string Text(JsonElement value, string at, string key) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 260 } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : throw Refused($"{at}: \"{key}\" must be a text");

    private static List<string> Texts(JsonElement value, string at, string key)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > MaxValues)
        {
            throw Refused($"{at}: \"{key}\" must be a list of texts");
        }

        return [.. value.EnumerateArray().Select(v => Text(v, at, key))];
    }

    private static OsVersionRange Range(JsonElement value, string at)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Refused($"{at}: \"axisOs\" must be an object with \"min\" and \"max\"");
        }

        string? min = null;
        string? max = null;
        foreach (var bound in value.EnumerateObject())
        {
            var text = bound.Name is "min" or "max" ? Text(bound.Value, at, "axisOs." + bound.Name) : throw Refused($"{at}: \"axisOs\" has the unknown key \"{Shorten(bound.Name)}\"");
            if (!AcapCompatibility.HasNumericVersion(text))
            {
                throw Refused($"{at}: \"axisOs.{bound.Name}\" must be a version such as 10.12");
            }

            (min, max) = bound.Name == "min" ? (text, max) : (min, text);
        }

        return min is null && max is null ? throw Refused($"{at}: \"axisOs\" needs \"min\" or \"max\"") : new OsVersionRange(min, max);
    }

    private static string Shorten(string text)
    {
        var builder = new StringBuilder(Math.Min(text.Length, 40));
        foreach (var c in text.Take(40))
        {
            builder.Append(char.IsControl(c) ? '?' : c);
        }

        return builder.ToString();
    }

    private static InvalidEapException Refused(string what) => new($"{FileName} {what}.");
}
