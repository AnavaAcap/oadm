using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Install certificates and Install CA certificates for AI agents (docs/spec/mcp.md "Secrets"): the PKCS#12 password is a
/// secret slot, never an input; the checks that need no file at their fields; the plan from the cached data (no file, over
/// HTTP); the task's own check that the certificate names the device; the CA plan lists each CA for the person to confirm.
/// </summary>
public sealed class AgentInstallTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static ITaskPlugin Task(PkiHarness pki, string id) => pki.Plugin.TaskPlugins.Single(t => t.Id == id);

    [Fact]
    public async Task The_pkcs12_password_is_a_slot_and_no_input()
    {
        await using var pki = await PkiHarness.StartAsync();
        var install = Task(pki, PkiTaskIds.Install);
        var payload = PkiJson.Serialize(new InstallPayload { Files = [new InstallFile(Guid.NewGuid(), "f1", "cam.pfx")] });

        Assert.Equal([new TaskSecret("password", "PKCS#12 password", "password")], install.SecretsOf(payload));
        Assert.Empty(install.SecretsOf(PkiJson.Serialize(new InstallPayload { WithoutPassword = true })));

        // The host leaves the [Secret] property out of the schema of the agents.
        Assert.Contains(typeof(InstallPayload).GetProperty(nameof(InstallPayload.Password))!.GetCustomAttributes(typeof(SecretAttribute), false), a => ((SecretAttribute)a).Label == "PKCS#12 password");
    }

    [Fact]
    public async Task The_checks_that_need_no_file_answer_at_their_fields()
    {
        await using var pki = await PkiHarness.StartAsync();
        var install = Task(pki, PkiTaskIds.Install);
        var device = Guid.NewGuid();

        Assert.Equal("purpose", Assert.Single(install.ValidatePayload(PkiJson.Serialize(new InstallPayload { Purpose = "tls" }), Caller)).Field);
        Assert.Equal("files", Assert.Single(install.ValidatePayload(PkiJson.Serialize(new InstallPayload()), Caller)).Field);
        Assert.Equal("files[0].deviceId", Assert.Single(install.ValidatePayload(PkiJson.Serialize(new InstallPayload { Files = [new InstallFile(Guid.Empty, "f1", "a.pfx")] }), Caller)).Field);
        Assert.Equal("files[1].deviceId", Assert.Single(install.ValidatePayload(PkiJson.Serialize(new InstallPayload
        {
            Files = [new InstallFile(device, "f1", "a.pfx"), new InstallFile(device, "f2", "b.pfx")],
        }), Caller)).Field);
        Assert.Equal("files[0].fileId", Assert.Single(install.ValidatePayload(PkiJson.Serialize(new InstallPayload { Files = [new InstallFile(device, " ", "a.pfx")] }), Caller)).Field);
        Assert.Empty(install.ValidatePayload(PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.CaOnly, Files = [new InstallFile(Guid.Empty, "f1", "a.p12")] }), Caller));
    }

    [Fact]
    public async Task The_plan_skips_a_device_without_its_file_and_one_over_http_unless_the_keys_may_go()
    {
        await using var pki = await PkiHarness.StartAsync();
        var install = Task(pki, PkiTaskIds.Install);
        var withFile = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "ACCC8E000001") { ConnectsOverHttps = true };
        var overHttp = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.49", "ACCC8E000002") { ConnectsOverHttps = false };
        var without = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.50", "ACCC8E000003");
        var files = new MemoryFiles();
        files.Files["f1"] = ("cam48.pfx", [1, 2, 3]);
        var payload = new InstallPayload { Files = [new InstallFile(withFile.Id, "f1", "cam48.pfx"), new InstallFile(overHttp.Id, "f2", "cam49.pfx")] };

        var plan = await install.PlanAsync(PkiJson.Serialize(payload), [withFile, overHttp, without], files, CancellationToken.None);
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.Equal(InstallCertificatesTask.OverHttpSkip, plan.Devices[1].SkipReason);
        Assert.Equal(InstallCertificatesTask.NoFile, plan.Devices[2].SkipReason);
        Assert.Equal("cam48.pfx", Assert.Single(plan.Files).Name);
        Assert.Contains(InstallCertificatesTask.FileMissing, plan.Warnings); // f2 is not among the uploads
        Assert.Contains(InstallCertificatesTask.MatchChecked, plan.Warnings);

        var anyway = await install.PlanAsync(PkiJson.Serialize(payload with { SendKeysOverHttp = true }), [withFile, overHttp], files, CancellationToken.None);
        Assert.Null(anyway.Devices[1].SkipReason);
        Assert.Contains("1 device is reached over HTTP: its private key crosses the network unencrypted.", anyway.Warnings);
    }

    [Fact]
    public async Task The_task_refuses_a_certificate_that_names_another_device_before_any_write()
    {
        await using var h = await TaskHarness.StartAsync();
        using var ca = TestCa.Root("Customer CA");
        using var other = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.99");
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.Https, Password = "secret", Files = [new InstallFile(h.Device.Id, "f1", "cam99.pfx")] });

        var refused = await RenewDeleteInstallTests.RunWithFileAsync(h, payload, TestCa.Pfx("secret", other));

        Assert.Contains(InstallCertificatesTask.NotThisDevice, refused!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Writes);
    }

    [Fact]
    public async Task The_ca_plan_lists_each_ca_with_its_fingerprint_and_the_trust_warning_first()
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = Task(pki, PkiTaskIds.InstallCa);
        using var root = TestCa.Root("Customer Root CA");
        var pem = root.ExportCertificatePem().ReplaceLineEndings(" "); // one line: the tools take no line breaks
        var payload = PkiJson.Serialize(new InstallCaPayload { Certificates = [new CaCertificatePayload("Customer", pem)] });
        var device = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.48", "ACCC8E000001");

        Assert.Empty(task.ValidatePayload(payload, Caller));
        var plan = await task.PlanAsync(payload, [device], CancellationToken.None);

        Assert.Equal(InstallCaTask.TrustWarning, plan.Warnings[0]);
        var line = plan.Warnings[1];
        Assert.StartsWith("CA Customer: issued to ", line, StringComparison.Ordinal);
        Assert.Contains("Customer Root CA", line, StringComparison.Ordinal);
        Assert.Contains(Ca.CaCertificates.ColonFingerprint(Ca.CaCertificates.Fingerprint(root)), line, StringComparison.Ordinal);
        Assert.Contains("valid until " + root.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
    }
}
