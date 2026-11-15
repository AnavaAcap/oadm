using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Pki.Tasks;

/// <summary>
/// The input of AI agents (SDK 0.13.0, docs/spec/mcp.md "Secrets"): the dialog's <see cref="InstallPayload"/> with the files
/// from upload links by device id; its PKCS#12 password is entered by the person (the slot <c>password</c>, "PKCS#12
/// password"), never sent by the agent. The dialog reads the files with the password to match them; the preview cannot (it
/// has no password yet), so the agent names the device of each file and the task checks that the certificate names that
/// device before it installs it. No clock question: a device whose clock is behind the certificate refuses, as in the
/// dialog's 802.1X install.
/// </summary>
public sealed partial class InstallCertificatesTask
{
    public const string NotThisDevice = "The certificate does not name this device (its MAC address, IP address or host name).";
    public const string NoFile = "No certificate file for this device.";
    public const string OverHttpSkip = "Reached over HTTP: its private key would cross the network unencrypted.";
    public const string FileMissing = "A file of this install is not among your uploads. Upload it again.";
    public const string MatchChecked = "Each device checks that its certificate names it (MAC address, IP address or host name) before the install.";
    public const string HttpsSwitch = "The web server of each device switches to its certificate.";
    public const string CaOnlyLine = "Every certificate of the files goes to the devices as a CA certificate: they will trust what these CAs issue.";

    /// <inheritdoc />
    public override Type? PayloadType => typeof(InstallPayload);

    /// <summary>The PKCS#12 password, unless the files have none.</summary>
    public IReadOnlyList<TaskSecret> SecretsOf(string? payloadJson) =>
        PkiJson.TryDeserialize<InstallPayload>(payloadJson) is { WithoutPassword: true } ? [] : [new TaskSecret("password", "PKCS#12 password", "password")];

    /// <summary>The dialog's rules that need no file: the purpose, at least one file, one per device, the 802.1X policy.</summary>
    public override IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
    {
        InstallPayload payload;
        try
        {
            payload = PkiJson.Deserialize<InstallPayload>(payloadJson);
        }
        catch (ArgumentException ex)
        {
            return [new(string.Empty, ex.Message)];
        }

        if (payload.Purpose is not (InstallPurpose.Https or InstallPurpose.Dot1x or InstallPurpose.CaOnly))
        {
            return [new("purpose", "Choose https, dot1x or ca.")];
        }

        var files = payload.Files ?? [];
        if (files.Count == 0)
        {
            return [new("files", "Choose at least one certificate file.")];
        }

        var errors = new List<PayloadError>();
        var seen = new HashSet<Guid>();
        for (var i = 0; i < files.Count && errors.Count < 20; i++)
        {
            var file = files[i];
            if (file is null || string.IsNullOrWhiteSpace(file.FileId))
            {
                errors.Add(new($"files[{i}].fileId", "Enter the file id of an upload."));
            }
            else if (payload.Purpose != InstallPurpose.CaOnly && file.DeviceId == Guid.Empty)
            {
                errors.Add(new($"files[{i}].deviceId", "Choose the device of this file."));
            }
            else if (payload.Purpose != InstallPurpose.CaOnly && !seen.Add(file.DeviceId))
            {
                errors.Add(new($"files[{i}].deviceId", "A device gets one file."));
            }
        }

        if (errors.Count == 0 && payload.Purpose == InstallPurpose.Dot1x)
        {
            errors.AddRange(PolicyErrors(Policies.PolicyKinds.Dot1xCertificate, payload.PolicyId, "policyId", firstIfNone: true));
        }

        return errors;
    }

    /// <summary>The plan of the dialog's checks that need no file content.</summary>
    public override Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, CancellationToken ct) =>
        PlanAsync(payloadJson, devices, null, ct);

    /// <summary>
    /// From the payload and the cached data: a device without its file is skipped (HTTPS, 802.1X), one reached over HTTP is
    /// skipped unless the person agreed to send the keys anyway; the files with their SHA-256, and the dialog's texts.
    /// </summary>
    public Task<TaskPlan> PlanAsync(string? payloadJson, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles? files, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        return PlanCoreAsync(PkiJson.Deserialize<InstallPayload>(payloadJson), devices, files, ct);
    }

    private async Task<TaskPlan> PlanCoreAsync(InstallPayload payload, IReadOnlyList<IDeviceInfo> devices, IUploadedFiles? files, CancellationToken ct)
    {
        var caOnly = payload.Purpose == InstallPurpose.CaOnly;
        var withFile = payload.Files.Select(f => f.DeviceId).ToHashSet();
        var planned = new List<TaskPlanDevice>(devices.Count);
        var overHttp = 0;
        foreach (var device in devices)
        {
            if (!CanRun(device))
            {
                planned.Add(new TaskPlanDevice(device.Id, NotSupportedReason(device) ?? TaskSupportReasons.Default));
            }
            else if (!caOnly && !withFile.Contains(device.Id))
            {
                planned.Add(new TaskPlanDevice(device.Id, NoFile));
            }
            else if (!caOnly && device.ConnectsOverHttps == false && !payload.SendKeysOverHttp)
            {
                planned.Add(new TaskPlanDevice(device.Id, OverHttpSkip));
            }
            else
            {
                planned.Add(new TaskPlanDevice(device.Id));
                overHttp += !caOnly && device.ConnectsOverHttps == false ? 1 : 0;
            }
        }

        var named = new List<TaskPlanFile>();
        var missing = false;
        if (files is not null)
        {
            foreach (var id in payload.Files.Select(f => f.FileId).Distinct(StringComparer.Ordinal))
            {
                if (await files.FindAsync(id, ct).ConfigureAwait(false) is { } file)
                {
                    named.Add(new TaskPlanFile(file.Name, file.Sha256));
                }
                else
                {
                    missing = true;
                }
            }
        }

        var warnings = new List<string>
        {
            payload.Purpose switch
            {
                InstallPurpose.Https => HttpsSwitch,
                InstallPurpose.Dot1x => PkiTaskIds.Dot1xWarning,
                _ => CaOnlyLine,
            },
        };
        if (!caOnly)
        {
            warnings.Add(MatchChecked);
        }

        if (overHttp > 0)
        {
            warnings.Add(overHttp == 1
                ? "1 device is reached over HTTP: its private key crosses the network unencrypted."
                : string.Create(CultureInfo.InvariantCulture, $"{overHttp:N0} devices are reached over HTTP: their private keys cross the network unencrypted."));
        }

        if (missing)
        {
            warnings.Add(FileMissing);
        }

        return new TaskPlan(planned) { Files = named, Warnings = warnings };
    }
}
