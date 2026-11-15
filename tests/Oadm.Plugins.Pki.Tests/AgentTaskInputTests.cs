using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// View and Delete certificates for AI agents (SDK 0.13.0, docs/spec/mcp.md "Tasks"): the payload types, the payload checks
/// with their fields and texts, the plans from the cached data, the read of View, and that nothing is written on invalid input
/// or on a device without the certificate API.
/// </summary>
public sealed class AgentTaskInputTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static string Delete(params (Guid Device, CertificateRef[] Refs)[] devices) =>
        PkiJson.Serialize(new DeletePayload { Devices = devices.ToDictionary(d => d.Device, d => (IReadOnlyList<CertificateRef>)d.Refs) });

    [Fact]
    public async Task View_and_delete_have_a_payload_type_and_install_stays_not_offered()
    {
        await using var pki = await PkiHarness.StartAsync();
        ITaskPlugin Task(string id) => pki.Plugin.TaskPlugins.Single(t => t.Id == id);

        Assert.Equal(typeof(PkiViewMcpPayload), Task(PkiTaskIds.View).PayloadType);
        Assert.Equal(typeof(DeletePayload), Task(PkiTaskIds.Delete).PayloadType);
        Assert.Same(PkiJson.Options, Task(PkiTaskIds.Delete).PayloadJsonOptions);
        Assert.Null(Task(PkiTaskIds.Install).PayloadType); // a password
    }

    [Fact]
    public async Task Delete_refuses_a_payload_without_a_certificate_a_name_or_with_a_factory_certificate_at_the_device_field()
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = pki.Plugin.TaskPlugins.Single(t => t.Id == PkiTaskIds.Delete);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        Assert.Equal([new PayloadError("devices", DeleteTexts.NoneChosen)], task.ValidatePayload("{}", Caller));
        Assert.Equal([new PayloadError("devices", DeleteTexts.NoneChosen)], task.ValidatePayload(Delete((a, [])), Caller));
        Assert.Equal("", Assert.Single(task.ValidatePayload("{\"devices\":", Caller)).Field);

        var errors = task.ValidatePayload(Delete(
            (a, [new CertificateRef("old one", false)]),
            (b, [new CertificateRef(" ", false)]),
            (c, [new CertificateRef("Axis device ID RSA-2048 (802.1AR)", false)])), Caller);
        Assert.Equal(
        [
            new PayloadError("devices." + b, DeleteTexts.NameMissing),
            new PayloadError("devices." + c, "\"Axis device ID RSA-2048 (802.1AR)\" is an Axis factory certificate and cannot be deleted."),
        ], errors);

        var many = Enumerable.Range(0, DeletePayload.MaxPerDevice + 1).Select(i => new CertificateRef("c" + i, false)).ToArray();
        Assert.Equal([new PayloadError("devices." + a, DeleteTexts.TooMany)], task.ValidatePayload(Delete((a, many)), Caller));
        Assert.Empty(task.ValidatePayload(Delete((a, [new CertificateRef("old one", false), new CertificateRef("COMODO ECC Certification Authority", true)])), Caller));
    }

    [Fact]
    public async Task The_delete_plan_counts_what_goes_skips_devices_without_a_choice_and_warns_loudly()
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = pki.Plugin.TaskPlugins.Single(t => t.Id == PkiTaskIds.Delete);
        var chosen = new PkiFakeDevice(Guid.NewGuid());
        var second = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.49");
        var none = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.50");
        var old = new PkiFakeDevice(Guid.NewGuid(), "10.0.0.51", Firmware: "6.50.5");
        var payload = Delete(
            (chosen.Id, [new CertificateRef("old one", false), new CertificateRef("COMODO ECC Certification Authority", true), new CertificateRef("old one", false)]),
            (second.Id, [new CertificateRef("old one", false)]),
            (old.Id, [new CertificateRef("old one", false)]));

        var plan = await task.PlanAsync(payload, [chosen, second, none, old], CancellationToken.None);

        Assert.Equal([chosen.Id, second.Id, none.Id, old.Id], plan.Devices.Select(d => d.DeviceId));
        Assert.Null(plan.Devices[0].SkipReason);
        Assert.Null(plan.Devices[1].SkipReason);
        Assert.Equal(DeleteTexts.NotChosen, plan.Devices[2].SkipReason);
        Assert.Equal(task.NotSupportedReason(old), plan.Devices[3].SkipReason);
        Assert.StartsWith("Needs AXIS OS 6.53", plan.Devices[3].SkipReason, StringComparison.Ordinal);
        Assert.Equal(["Deletes 3 certificates from 2 devices. A deleted certificate and its key cannot be restored.", DeleteTexts.InUseNote], plan.Warnings);

        var nothing = await task.PlanAsync(payload, [none], CancellationToken.None);
        Assert.Empty(nothing.Warnings);
    }

    [Fact]
    public async Task An_invalid_delete_and_one_on_a_device_without_the_certificate_api_change_nothing()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.AddCertificate("old one", h.Camera.CertificatePem("Site Device HTTPS lUZuIXuu"));

        // A factory certificate: refused by the payload check, and by the device read before any delete.
        var factory = Delete((h.Device.Id, [new CertificateRef("Axis device ID RSA-2048 (802.1AR)", false), new CertificateRef("old one", false)]));
        Assert.NotEmpty(h.Task<DeleteCertificatesTask>(PkiTaskIds.Delete).ValidatePayload(factory, Caller));
        var ex = await h.RunAsync(PkiTaskIds.Delete, factory);
        Assert.EndsWith("Nothing was changed.", ex!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Writes);
        Assert.Contains("old one", h.Camera.CertificateAliases);

        // AXIS OS before 6.53 and no certificate API: the compatibility check refuses before the first write.
        h.Camera.HasCertApi = false;
        h.Camera.Firmware = "6.50.5";
        PkiCompatibility.ClearCache();
        ex = await h.RunAsync(PkiTaskIds.Delete, Delete((h.Device.Id, [new CertificateRef("old one", false)])));
        Assert.IsType<DeviceNotCompatibleException>(ex);
        Assert.EndsWith("Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Camera.Writes);
        Assert.Contains("old one", h.Camera.CertificateAliases);
    }

    [Fact]
    public async Task View_runs_as_a_read_and_logs_every_certificate_by_the_name_delete_takes()
    {
        await using var h = await TaskHarness.StartAsync();
        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable));
        var writes = h.Camera.Writes.Count;
        var task = h.Task<ViewCertificatesTask>(PkiTaskIds.View);
        Assert.Empty(task.ValidatePayload("{}", Caller));
        var plan = await task.PlanAsync("{}", [h.Device], CancellationToken.None);
        Assert.Null(Assert.Single(plan.Devices).SkipReason);
        Assert.Empty(plan.Warnings);

        Assert.Null(await h.RunAsync(PkiTaskIds.View, "{}"));

        Assert.Equal(["Check compatibility: Done", "Read certificates: Done", "Completed: Done"], h.Lines);
        Assert.Equal(writes, h.Camera.Writes.Count); // read only
        var lines = h.Context.LogEntries.Select(e => e.Message).ToList();
        var web = Assert.Single(lines, l => l.StartsWith(h.Camera.WebServerAlias + ": ", StringComparison.Ordinal));
        Assert.Contains("server certificate", web, StringComparison.Ordinal);
        Assert.Contains("in use by HTTPS", web, StringComparison.Ordinal);
        Assert.Contains("from OADM", web, StringComparison.Ordinal);
        Assert.Contains("SHA-256 ", web, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("Axis device ID", StringComparison.Ordinal) && l.Contains("Axis factory certificate", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("CA certificate", StringComparison.Ordinal) && l.Contains("not in use", StringComparison.Ordinal));
        Assert.Equal(h.Camera.CertificateAliases.Count + h.Camera.CaAliases.Count, lines.Count);
        Assert.Contains(lines.Count + " certificates", h.Detail(PkiSteps.ReadCertificates), StringComparison.Ordinal);

        // The API check refuses a device without the certificate API, as Delete does.
        h.Camera.HasCertApi = false;
        h.Camera.Firmware = "6.50.5";
        PkiCompatibility.ClearCache();
        Assert.IsType<DeviceNotCompatibleException>(await h.RunAsync(PkiTaskIds.View, "{}"));
    }
}
