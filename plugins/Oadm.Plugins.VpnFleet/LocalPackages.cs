using Oadm.Plugins.Acap;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VpnFleet;

/// <summary>A package the user uploaded from their computer (the page's upload): its upload id and file name.</summary>
public sealed record PackageFile(string FileId, string Name);

/// <summary>
/// Installs without internet: the .eap files of a release the user downloaded, uploaded from the page. Read with the .eap
/// reader of Applications (ACAP); each device gets the build that fits it (<see cref="AcapBuildChooser"/>).
/// </summary>
public static class LocalPackages
{
    /// <summary>At most this many packages in one install (the builds of one release: architectures, ACAP 3).</summary>
    public const int MaxFiles = 8;

    /// <summary>
    /// Why these packages cannot be installed together as <paramref name="app"/>, or null: another app, or several
    /// versions. The page checks the same before the upload.
    /// </summary>
    public static string? Problem(VpnApp app, IReadOnlyList<(string Name, EapManifest Package)> packages)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
        {
            return "Choose the packages.";
        }

        if (packages.Count > MaxFiles)
        {
            return $"Choose at most {MaxFiles} packages.";
        }

        if (packages.FirstOrDefault(p => !string.Equals(p.Package.AppName, app.AppName, StringComparison.OrdinalIgnoreCase)) is { Name: not null } other)
        {
            return $"{other.Name} is a package of {other.Package.DisplayName}, not {app.DisplayName}.";
        }

        var versions = packages.Select(p => p.Package.Version).Distinct(StringComparer.Ordinal).ToList();
        return versions.Count > 1 ? $"The packages have different versions ({string.Join(", ", versions)}). Choose the packages of one release." : null;
    }

    /// <summary>
    /// Reads the uploaded packages of the caller (or of a task) and checks them; throws with the text to show when one is
    /// gone, not a package, or does not fit the rule of <see cref="Problem"/>.
    /// </summary>
    public static async Task<IReadOnlyList<AcapBuild>> ReadAsync(IUploadedFiles uploads, VpnApp app, IReadOnlyList<PackageFile>? files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        if (files is not { Count: > 0 })
        {
            throw new ArgumentException("Choose the packages.");
        }

        if (files.Count > MaxFiles)
        {
            throw new ArgumentException($"Choose at most {MaxFiles} packages.");
        }

        var builds = new List<AcapBuild>();
        foreach (var file in files)
        {
            if (await uploads.FindAsync(file.FileId, ct).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException($"{file.Name}: the upload is gone. Choose the package again.");
            }

            EapManifest package;
            try
            {
                var stream = await uploads.OpenReadAsync(file.FileId, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    package = await EapReader.ReadAsync(stream, ct).ConfigureAwait(false);
                }
            }
            catch (InvalidEapException ex)
            {
                throw new ArgumentException($"{file.Name}: {ex.Message}", ex);
            }

            builds.Add(new AcapBuild(file.FileId, file.Name, package));
        }

        if (Problem(app, [.. builds.Select(b => (b.Name, b.Package))]) is { } problem)
        {
            throw new ArgumentException(problem);
        }

        return builds;
    }
}
