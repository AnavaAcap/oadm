using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Oadm.Plugins.Pki;

/// <summary>Ids and menu names of the contributed Security tasks (spec part 2), shared by server, dialogs and tests.</summary>
public static class PkiTaskIds
{
    public const string HttpsEnable = "oadm.pki.https-enable";
    public const string HttpsDisable = "oadm.pki.https-disable";
    public const string Dot1xEnable = "oadm.pki.dot1x-enable";
    public const string Dot1xDisable = "oadm.pki.dot1x-disable";
    public const string Renew = "oadm.pki.renew";
    public const string View = "oadm.pki.view";
    public const string Delete = "oadm.pki.delete";
    public const string Install = "oadm.pki.install";
    public const string InstallCa = "oadm.pki.install-ca";

    public const string HttpsEnableName = "Enable HTTPS";
    public const string HttpsDisableName = "Disable HTTPS";
    public const string Dot1xEnableName = "Enable IEEE 802.1X";
    public const string Dot1xDisableName = "Disable IEEE 802.1X";
    public const string RenewName = "Renew certificates";
    public const string ViewName = "View certificates";
    public const string DeleteName = "Delete certificates";
    public const string InstallName = "Install certificates";
    public const string InstallCaName = "Install CA certificates";

    /// <summary>Text of the HTTPS Disable confirmation.</summary>
    public const string HttpsDisableWarning = "Video systems that use HTTPS lose the connection to these devices.";

    /// <summary>Text of the 802.1X Enable confirmation.</summary>
    public const string Dot1xWarning = "Devices on ports that enforce 802.1X become unreachable if authentication fails.";
}

/// <summary>The texts of "Delete certificates", shared by its dialog, its payload check, its plan and its task.</summary>
public static class DeleteTexts
{
    /// <summary>The end of the dialog's confirmation and of the plan's sentence.</summary>
    public const string CannotRestore = "A deleted certificate and its key cannot be restored.";

    /// <summary>The payload chooses no certificate.</summary>
    public const string NoneChosen = "Choose at least one certificate.";

    /// <summary>A certificate of the payload without a name.</summary>
    public const string NameMissing = "Enter the certificate's name as View certificates lists it.";

    /// <summary>More certificates for one device than a payload takes.</summary>
    public static readonly string TooMany = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Choose at most {DeletePayload.MaxPerDevice} certificates per device.");

    /// <summary>A device of the run without a certificate in the payload (its task skips, the plan leaves it out).</summary>
    public const string NotChosen = "No certificate of this device was chosen";

    /// <summary>The plan's note: what the task refuses on the device (in use, factory, what uses it cannot be read).</summary>
    public const string InUseNote = "A certificate in use by HTTPS or 802.1X is never deleted: the task of that device fails and changes nothing.";

    /// <summary>An Axis factory device ID certificate (802.1AR): never deleted.</summary>
    public static string Factory(string alias) => $"\"{alias}\" is an Axis factory certificate and cannot be deleted.";

    /// <summary>A name the Axis factory device ID certificates have ("Axis device ID RSA-2048 (802.1AR)").</summary>
    public static bool IsFactoryName(string alias) => alias.Contains("(802.1AR)", StringComparison.Ordinal);

    /// <summary>"Deletes 3 certificates from 2 devices. A deleted certificate and its key cannot be restored."</summary>
    public static string Plan(int certificates, int devices) => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"Deletes {certificates:N0} {(certificates == 1 ? "certificate" : "certificates")} from {devices:N0} {(devices == 1 ? "device" : "devices")}. {CannotRestore}");
}

/// <summary>Query methods (<c>ITaskPluginQuery</c>) of the certificate dialogs; read-only.</summary>
public static class PkiQueries
{
    /// <summary>() -> <see cref="CertificateListReply"/> for one device.</summary>
    public const string ListCertificates = "listCertificates";

    /// <summary>
    /// () -> <see cref="IssuingPolicyList"/>: the issuing policies of the task's kind (Enable HTTPS: HTTPS certificate;
    /// Enable IEEE 802.1X, Renew certificates, Install certificates: IEEE 802.1X certificate), oldest first. Asked once with
    /// the first selected device.
    /// </summary>
    public const string ListIssuingPolicies = "listIssuingPolicies";
}

/// <summary>The answer of <see cref="PkiQueries.ListIssuingPolicies"/>.</summary>
public sealed record IssuingPolicyList
{
    /// <summary>The policies of the kind, oldest first (the first is preselected).</summary>
    public IReadOnlyList<Policies.PkiPolicy> Policies { get; init; } = [];

    /// <summary>The OADM CA's name ("OADM Root CA SERVER01"); null without a CA.</summary>
    public string? CaName { get; init; }

    /// <summary>The name of the CA each policy issues with ("Issued by"), by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> CaNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The name of the RADIUS server CA of each IEEE 802.1X policy, by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> RadiusCaNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>What each policy issues, its summary sentence (below the dialog's Policy field), by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> Summaries { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Why there are none (the policies cannot be read, the PKI does not run); null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Payload of Enable HTTPS and Enable IEEE 802.1X: the issuing policy the dialog chose; the run uses its values as stored
/// (decision 2026-10-25: no values of their own in the dialog, the policy decides). No policy: the first of the kind.
/// Payloads are never stored.
/// </summary>
public sealed record IssuePayload
{
    /// <summary>The user answered the clock question with Yes (<c>CameraClock</c>).</summary>
    public bool ClockConfirmed { get; init; }

    public Guid? PolicyId { get; init; }
}

/// <summary>
/// Payload of "Renew certificates": the IEEE 802.1X certificate policy the dialog chose (HTTPS renews with the first HTTPS
/// certificate policy) and the clock answer. No policy id (older payloads): the first IEEE 802.1X policy. Never stored.
/// </summary>
public sealed record RenewPayload
{
    /// <summary>The user answered the clock question with Yes (<c>CameraClock</c>).</summary>
    public bool ClockConfirmed { get; init; }

    /// <summary>The IEEE 802.1X certificate policy of the renewal.</summary>
    public Guid? Dot1xPolicyId { get; init; }
}

/// <summary>Group of an installed certificate in the view and delete dialogs.</summary>
public static class CertificateKind
{
    public const string Client = "client";
    public const string Server = "server";
    public const string Ca = "ca";
}

/// <summary>One certificate installed on a device, as the dialogs show it.</summary>
public sealed record InstalledCertificate
{
    public string Alias { get; init; } = string.Empty;

    /// <summary><see cref="CertificateKind"/>.</summary>
    public string Kind { get; init; } = CertificateKind.Server;

    /// <summary>Common name of the issuer.</summary>
    public string IssuedBy { get; init; } = string.Empty;

    /// <summary>Common name of the subject.</summary>
    public string IssuedTo { get; init; } = string.Empty;

    public DateTime NotAfterUtc { get; init; }

    /// <summary>"HTTPS", "802.1X" (the services using it), empty when unused or when <see cref="UseUnknown"/>.</summary>
    public IReadOnlyList<string> InUse { get; init; } = [];

    /// <summary>
    /// The web server or 802.1X settings could not be read: what uses it is unknown, so it cannot be deleted (device safety:
    /// it could be the certificate that keeps the camera on the network).
    /// </summary>
    public bool UseUnknown { get; init; }

    /// <summary>The In use text of a certificate whose use cannot be read.</summary>
    public const string UnknownUseText = "Unknown";

    /// <summary>OADM issued it (registry or alias).</summary>
    public bool FromOadm { get; init; }

    /// <summary>An Axis factory device ID certificate (802.1AR): never deletable.</summary>
    public bool Factory { get; init; }

    /// <summary>SHA-256 (upper hex).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>The certificate's serial number (upper hex); empty when it could not be read.</summary>
    public string SerialNumber { get; init; } = string.Empty;
}

/// <summary>Answer of <see cref="PkiQueries.ListCertificates"/>.</summary>
public sealed record CertificateListReply
{
    public IReadOnlyList<InstalledCertificate> Certificates { get; init; } = [];

    /// <summary>The device cannot be read or is not compatible (user text).</summary>
    public string? Error { get; init; }
}

/// <summary>One certificate to delete.</summary>
public sealed record CertificateRef(
    [property: Description("The certificate's name on the device, as View certificates lists it."), MaxLength(CertificateRef.MaxAliasLength)] string Alias,
    [property: Description("True for a CA certificate, false for a client or server certificate.")] bool Ca)
{
    /// <summary>The longest name a payload takes (the devices' names are far shorter).</summary>
    public const int MaxAliasLength = 256;
}

/// <summary>
/// Payload of "Delete certificates": the aliases per device id (the server re-checks "in use"). Also the input for AI agents
/// (SDK 0.13.0), who read the names with View certificates first.
/// </summary>
[Description("Delete certificates and their keys from devices. Read their names with View certificates first. A certificate in use by HTTPS or 802.1X, or an Axis factory certificate, is never deleted.")]
public sealed record DeletePayload
{
    /// <summary>The most certificates of one device a payload takes.</summary>
    public const int MaxPerDevice = 100;

    [Description("The certificates to delete, by device id.")]
    public IReadOnlyDictionary<Guid, IReadOnlyList<CertificateRef>> Devices { get; init; } = new Dictionary<Guid, IReadOnlyList<CertificateRef>>();
}

/// <summary>What "Install certificates" sets up with the installed certificate.</summary>
public static class InstallPurpose
{
    public const string Https = "https";
    public const string Dot1x = "dot1x";

    /// <summary>The certificates of the file go to the device as CA certificates only.</summary>
    public const string CaOnly = "ca";
}

/// <summary>One file of "Install certificates", matched to one device.</summary>
public sealed record InstallFile(Guid DeviceId, string FileId, string FileName);

/// <summary>Payload of "Install certificates". The password stays in memory (payloads are never persisted).</summary>
public sealed record InstallPayload
{
    /// <summary><see cref="InstallPurpose"/>.</summary>
    public string Purpose { get; init; } = InstallPurpose.Https;

    /// <summary>One password for all files (like ADM).</summary>
    public string? Password { get; init; }

    public IReadOnlyList<InstallFile> Files { get; init; } = [];

    // Kept beside the record (not a field of it): two payloads with the same values stay equal.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<InstallPayload, ILookup<Guid, (InstallFile File, int Index)>> ByDevice = new();

    /// <summary>
    /// The files of one device in the order of <see cref="Files"/> (<see cref="Guid.Empty"/>: the files for every device,
    /// "CA certificates only"). Indexed once per payload: thousands of devices do not search thousands of files each.
    /// </summary>
    public IEnumerable<(InstallFile File, int Index)> FilesFor(Guid deviceId) =>
        ByDevice.GetValue(this, static payload => payload.Files.Select((file, index) => (file, index)).ToLookup(f => f.file.DeviceId))[deviceId];

    /// <summary>
    /// IEEE 802.1X: the IEEE 802.1X certificate policy whose settings and RADIUS server CA the run sets (the file brings the
    /// certificate). Null (older payloads): the first of the kind.
    /// </summary>
    public Guid? PolicyId { get; init; }

    /// <summary>
    /// The user's answer to "Send the keys anyway": a file with its private key may go to a device OADM reaches over HTTP.
    /// False (also a payload without it, from an older client): not answered, such a device is refused
    /// (<see cref="KeysOverHttpRefused"/>).
    /// </summary>
    public bool SendKeysOverHttp { get; init; }

    /// <summary>The task's refusal for a device reached over HTTP without <see cref="SendKeysOverHttp"/>.</summary>
    public const string KeysOverHttpRefused =
        "The device is reached over HTTP: its private key would cross the network unencrypted. Start the task again and tick Send the keys anyway. Nothing was changed.";
}

/// <summary>One CA certificate of "Install CA certificates": the name for the step and the PEM.</summary>
public sealed record CaCertificatePayload(
    [property: Description("A name for the certificate, shown in the task's steps."), MinLength(1), MaxLength(128)] string Name,
    [property: Description("The CA certificate in PEM text (certificates only, never a private key)."), MinLength(1), MaxLength(CaCertificatePayload.MaxPemLength)] string Pem)
{
    /// <summary>The longest PEM text of one certificate (a CA certificate is a few KB).</summary>
    public const int MaxPemLength = 16 * 1024;
}

/// <summary>
/// Payload of "Install CA certificates": the CA certificates for every selected device (public data only, a few KB each,
/// so no upload). The task checks every one again before the first write.
/// </summary>
[Description("Install CA certificates on devices.")]
public sealed record InstallCaPayload
{
    [Description("The CA certificates, at most 20."), MinLength(1), MaxLength(20)]
    public IReadOnlyList<CaCertificatePayload> Certificates { get; init; } = [];
}

/// <summary>The input of a Security task without settings (Disable HTTPS, Disable IEEE 802.1X) for AI agents.</summary>
[Description("This task takes no settings.")]
public sealed record PkiNoSettingsPayload;

/// <summary>The input of View certificates for AI agents: no settings; the task's log lists the certificates.</summary>
[Description("Read the certificates of the devices: one line per certificate in the task's log (name, kind, issued to and by, valid until, in use by, SHA-256). Nothing is changed.")]
public sealed record PkiViewMcpPayload;

/// <summary>
/// The input of Enable HTTPS and Enable IEEE 802.1X for AI agents: the issuing policy. The clock question of the dialog is not
/// asked over MCP: a device whose clock is behind refuses the task ("Nothing was changed"); fix the clock or run it in OADM.
/// </summary>
[Description("Issue a certificate by a policy of the PKI page and switch the device to it.")]
public sealed record PkiIssueMcpPayload(
    [property: Description("The id of the issuing policy of this kind; leave it out for the first one.")] Guid? PolicyId = null);

/// <summary>The input of Renew certificates for AI agents (the IEEE 802.1X policy; HTTPS renews with the first HTTPS policy).</summary>
[Description("Renew the certificates OADM issued on the devices.")]
public sealed record PkiRenewMcpPayload(
    [property: Description("The id of the IEEE 802.1X certificate policy; leave it out for the first one.")] Guid? Dot1xPolicyId = null);
