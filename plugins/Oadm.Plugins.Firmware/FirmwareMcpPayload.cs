using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// The input of "Upgrade AXIS OS" for AI agents (docs/spec/mcp.md): AXIS OS files uploaded with an upload link, never a version
/// from axis.com (that needs the user's MyAxis sign-in). It is the dialog's <see cref="FirmwarePayload"/> without the fields
/// that only name the task or need that sign-in, so the same payload text is read by the task.
/// </summary>
[Description("Upgrade AXIS OS with files you uploaded (oadm_files_upload_link).")]
public sealed record FirmwareMcpPayload(
    [property: Description("The uploaded AXIS OS files, one per model. A file without devices goes to every device of the model named in its file name."),
        MinLength(1), MaxLength(FirmwareMcpPayload.MaxFiles)] IReadOnlyList<FirmwareMcpFile> Files,
    [property: Description("None keeps all settings. Soft resets settings, users and passwords, keeping the network settings. Hard resets everything, including the IP address.")]
    FactoryDefaultMode FactoryDefaultMode = FactoryDefaultMode.None,
    [property: Description("Allow installing an older version than the one on the device. It needs a soft or hard factory default.")] bool AllowDowngrade = false)
{
    /// <summary>Files of one run at most (one per model).</summary>
    public const int MaxFiles = 64;
}

/// <summary>One uploaded AXIS OS file of <see cref="FirmwareMcpPayload"/>.</summary>
public sealed record FirmwareMcpFile(
    [property: Description("The file id the upload answered."), MinLength(1), MaxLength(64)] string FileId,
    [property: Description("The file name as uploaded, like P3265-V_12_11_77.bin. The model and the version are read from it."), MinLength(1), MaxLength(255)] string FileName,
    [property: Description("The devices (their ids) that get this file."), MaxLength(DeviceLimits.MaxDevices)] IReadOnlyList<Guid>? Devices = null);
