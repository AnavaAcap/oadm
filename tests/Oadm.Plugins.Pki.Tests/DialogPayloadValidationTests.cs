using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The server runs <c>ValidatePayload</c> of a task before any task of any client starts, so every payload a Security task's
/// dialog can send must pass it. Each dialog's view model is driven to the payload it completes with (the same type and
/// serializer the dialog window uses), and the exact JSON is handed to the task's validator.
/// </summary>
public sealed class DialogPayloadValidationTests
{
    private static readonly ICallContext Caller = FakeCallContext.AllDevices(new FakeCoreContext());

    private static readonly IReadOnlyList<IDeviceInfo> Devices = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48" }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }];

    private static ITaskPlugin Task(PkiHarness pki, string id) => pki.Plugin.TaskPlugins.Single(t => t.Id == id);

    private static void AssertAccepted(ITaskPlugin task, string? json)
    {
        var errors = task.ValidatePayload(json, Caller);
        Assert.True(errors.Count == 0, string.Join(" | ", errors.Select(e => e.Field + ": " + e.Message)) + " in " + json);
    }

    private static async Task<PkiPolicy> AddAsync(PkiHarness pki, PkiPolicy policy)
    {
        var reply = await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, policy);
        Assert.Null(reply.Error);
        return reply.Saved!;
    }

    /// <summary>The policies the dialog lists: the built-in one and one more per key type and validity.</summary>
    private static async Task AddPoliciesAsync(PkiHarness pki, string kind)
    {
        foreach (var (name, days, key) in new[] { ("Two years RSA", 730, PolicyKeys.Rsa), ("One year ECC", 365, PolicyKeys.Ecc), ("Short", 30, PolicyKeys.Ecc) })
        {
            var builtIn = pki.Service.Policies.FirstOf(kind)!;
            await AddAsync(pki, builtIn with { Id = Guid.Empty, Name = name + " " + kind, ValidDays = days, Key = key });
        }
    }

    // ---- Enable HTTPS, Enable IEEE 802.1X ----

    [Theory]
    [InlineData(PkiTaskIds.HttpsEnable, PolicyKinds.HttpsCertificate)]
    [InlineData(PkiTaskIds.Dot1xEnable, PolicyKinds.Dot1xCertificate)]
    public async Task Every_policy_the_enable_dialog_offers_passes_with_both_answers_to_the_clock_question(string taskId, string kind)
    {
        await using var pki = await PkiHarness.StartAsync();
        await AddPoliciesAsync(pki, kind);
        var task = Task(pki, taskId);
        var ctx = new IssuingDialogContext(pki.Service, kind);
        var offered = 0;

        for (var index = 0; ; index++)
        {
            var vm = new IssueCertificateViewModel(ctx, Devices, kind);
            await vm.LoadAsync(CancellationToken.None);
            if (index >= vm.Choice.Policies.Count)
            {
                break;
            }

            vm.Choice.Policy = vm.Choice.Policies[index];
            Assert.True(vm.StartCommand.CanExecute(null));
            vm.StartCommand.Execute(null);
            var payload = vm.Payload!;
            foreach (var confirmed in new[] { false, true })
            {
                AssertAccepted(task, PkiJson.Serialize(payload with { ClockConfirmed = confirmed }));
            }

            offered++;
        }

        Assert.True(offered >= 4);
    }

    [Theory]
    [InlineData(PkiTaskIds.HttpsEnable)]
    [InlineData(PkiTaskIds.Dot1xEnable)]
    public async Task A_payload_without_a_policy_passes_as_the_first_policy_of_older_clients(string taskId)
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = Task(pki, taskId);
        AssertAccepted(task, "{}");
        AssertAccepted(task, null);
        AssertAccepted(task, PkiJson.Serialize(new IssuePayload { ClockConfirmed = true }));
    }

    // ---- Renew certificates ----

    [Fact]
    public async Task Every_policy_the_renew_dialog_offers_passes_with_both_clock_answers_and_an_old_payload_too()
    {
        await using var pki = await PkiHarness.StartAsync();
        await AddPoliciesAsync(pki, PolicyKinds.Dot1xCertificate);
        var task = Task(pki, PkiTaskIds.Renew);
        var ctx = new IssuingDialogContext(pki.Service, PolicyKinds.Dot1xCertificate);
        var offered = 0;

        for (var index = 0; ; index++)
        {
            var vm = IssueCertificateViewModel.ForRenew(ctx, Devices);
            await vm.LoadAsync(CancellationToken.None);
            if (index >= vm.Choice.Policies.Count)
            {
                break;
            }

            vm.Choice.Policy = vm.Choice.Policies[index];
            vm.StartCommand.Execute(null);
            var chosen = vm.Payload!;
            foreach (var confirmed in new[] { false, true })
            {
                // exactly what RenewDialog sends
                AssertAccepted(task, PkiJson.Serialize(new RenewPayload { Dot1xPolicyId = chosen.PolicyId, ClockConfirmed = confirmed }));
            }

            offered++;
        }

        Assert.True(offered >= 4);
        AssertAccepted(task, "{}");
        AssertAccepted(task, null);
    }

    // ---- Disable HTTPS, Disable IEEE 802.1X ----

    [Fact]
    public async Task The_disable_tasks_pass_with_the_empty_payload_their_dialog_sends_and_with_none()
    {
        await using var pki = await PkiHarness.StartAsync();
        foreach (var id in new[] { PkiTaskIds.HttpsDisable, PkiTaskIds.Dot1xDisable })
        {
            AssertAccepted(Task(pki, id), "{}"); // HttpsDisableDialog
            AssertAccepted(Task(pki, id), null);
        }
    }

    /// <summary>The dialogs of these tasks have no rule the server repeats: the default check accepts what they send.</summary>
    [Fact]
    public async Task Tasks_without_a_check_of_their_own_accept_the_payloads_of_their_dialogs()
    {
        await using var pki = await PkiHarness.StartAsync();
        var install = PkiJson.Serialize(new InstallPayload());
        foreach (var id in new[] { PkiTaskIds.Install, PkiTaskIds.Delete, PkiTaskIds.View })
        {
            AssertAccepted(Task(pki, id), install);
            AssertAccepted(Task(pki, id), "{}");
            AssertAccepted(Task(pki, id), null);
        }
    }

    // ---- Install CA certificates ----

    [Fact]
    public async Task Install_CA_certificates_passes_for_pem_files_bundles_and_der_files_and_leaves_unusable_rows_out()
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = Task(pki, PkiTaskIds.InstallCa);
        using var root = TestCa.Root("Acme Root CA");
        using var issuing = TestCa.Intermediate(root);
        using var leaf = TestCa.Leaf(issuing);
        using var other = TestCa.Root("Customer CA");
        using var expired = TestCa.Root("Old CA", years: 1, notBefore: DateTimeOffset.UtcNow.AddYears(-3));
        var files = new Dictionary<string, byte[]>
        {
            ["root.pem"] = TestCa.Pem(root),
            ["bundle.crt"] = TestCa.Pem(root, issuing, leaf, expired), // a bundle: the leaf and the expired one are left out
            ["customer.der"] = other.RawData,
            ["notes.txt"] = TestCa.Ascii("not a certificate"),
        };
        var devices = new IDeviceInfo[] { new FakeDevice() };

        var single = new InstallCaCertificatesViewModel(devices, path => files[path]);
        single.Add(["root.pem"]);
        AssertAccepted(task, single.BuildPayload());

        var many = new InstallCaCertificatesViewModel(devices, path => files[path]);
        many.Add(["root.pem", "bundle.crt", "customer.der", "notes.txt"]);
        Assert.Equal(3, many.UsableCertificates.Count); // root, issuing and customer; the other rows have a problem
        AssertAccepted(task, many.BuildPayload());
        Assert.True(many.CanInstall);
    }

    [Fact]
    public async Task Install_CA_certificates_passes_at_the_largest_count_the_dialog_allows()
    {
        await using var pki = await PkiHarness.StartAsync();
        var task = Task(pki, PkiTaskIds.InstallCa);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var certificates = Enumerable.Range(0, CaCertificateFiles.MaxCertificates).Select(i => TestCa.Root("CA " + i, years: 1, rsa: key)).ToList();
        try
        {
            var files = new Dictionary<string, byte[]> { ["all.pem"] = TestCa.Pem([.. certificates]) };
            var vm = new InstallCaCertificatesViewModel([new FakeDevice()], path => files[path]);
            vm.Add(["all.pem"]);
            Assert.True(vm.CanInstall, vm.InstallBlockedReason);
            AssertAccepted(task, vm.BuildPayload());
        }
        finally
        {
            certificates.ForEach(c => c.Dispose());
        }
    }
}
