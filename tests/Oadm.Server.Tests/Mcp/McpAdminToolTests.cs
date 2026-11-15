using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Contracts.Updates;
using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.Licensing;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Settings;
using Oadm.Sdk.Licensing;
using Oadm.Server.AddOns;
using Oadm.Server.Assistant;
using Oadm.Server.Backup;
using Oadm.Server.Tests.AddOns;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tests.Updates;
using Oadm.Server.Tools;
using Oadm.Server.Updates;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The administration through agents (docs/spec/mcp.md "Administration", decision 2026-11-15): license and plugins, optional
/// plugins from an upload link, backups (schedule, back up now as a file, download, delete, restore) and the support report,
/// and Install now of a new version. Every change: Administrator only, the preview writes nothing, the run once with its own
/// handle, secrets only through the secret channel (never in an answer, the audit log or a log line), files through links,
/// the Assistant's card. Nothing real is installed: the release feed, the helper and the restart are fakes.
/// </summary>
public sealed class McpAdminToolTests
{
    /// <summary>The tools of this area (<see cref="McpToolTests"/> lists them with the others).</summary>
    public static readonly string[] ToolNames =
    [
        "oadm_backups", "oadm_backups_create_preview", "oadm_backups_create_run", "oadm_backups_delete_preview", "oadm_backups_delete_run",
        "oadm_backups_download", "oadm_backups_restore_preview", "oadm_backups_restore_run", "oadm_backups_schedule_preview", "oadm_backups_schedule_run",
        "oadm_license_import_preview", "oadm_license_import_run", "oadm_license_remove_preview", "oadm_license_remove_run", "oadm_license_trial_preview",
        "oadm_license_trial_run", "oadm_plugins_enable_preview", "oadm_plugins_enable_run", "oadm_plugins_license_preview", "oadm_plugins_license_remove_preview",
        "oadm_plugins_license_remove_run", "oadm_plugins_license_run", "oadm_plugins_optional", "oadm_plugins_optional_install_preview",
        "oadm_plugins_optional_install_run", "oadm_plugins_optional_remove_preview", "oadm_plugins_optional_remove_run", "oadm_plugins_optional_restart_preview",
        "oadm_plugins_optional_restart_run", "oadm_support_report", "oadm_update_install_preview", "oadm_update_install_run",
    ];

    /// <summary>The gRPC methods these tools call besides those of the other areas.</summary>
    public static readonly string[] Methods =
    [
        "/oadm.v1.BackupService/Create", "/oadm.v1.BackupService/Delete", "/oadm.v1.BackupService/Download", "/oadm.v1.BackupService/GetState",
        "/oadm.v1.BackupService/Inspect", "/oadm.v1.BackupService/Restore", "/oadm.v1.BackupService/SetSchedule",
        "/oadm.v1.LicenseService/ImportLicense", "/oadm.v1.LicenseService/RemoveLicense", "/oadm.v1.LicenseService/StartTrial",
        "/oadm.v1.PluginService/InstallAddOn", "/oadm.v1.PluginService/ListAddOns", "/oadm.v1.PluginService/RemoveAddOn",
        "/oadm.v1.PluginService/RemovePackageLicense", "/oadm.v1.PluginService/RestartForAddOns", "/oadm.v1.PluginService/SetPackageEnabled",
        "/oadm.v1.PluginService/SetPackageLicense", "/oadm.v1.PluginService/UploadAddOn",
        "/oadm.v1.SupportService/CreateReport", "/oadm.v1.UpdateService/Start",
    ];

    public static string[] RunTools => [.. ToolNames.Where(n => n.EndsWith("_run", StringComparison.Ordinal))];

    /// <summary>The runs that only add (<c>destructiveHint</c> false).</summary>
    public static readonly string[] AddingRuns = ["oadm_backups_create_run", "oadm_license_trial_run", "oadm_plugins_optional_install_run"];

    /// <summary>The runs that act outside OADM (Install now downloads the release).</summary>
    public static readonly string[] ActsOutside = ["oadm_update_install_run"];

    /// <summary>One call of every tool of this area for the scan of <see cref="McpSecretTests"/>.</summary>
    public static Dictionary<string, JsonObject> SecretScanCalls()
    {
        var calls = new Dictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            ["oadm_backups"] = [],
            ["oadm_backups_create_preview"] = [],
            ["oadm_backups_delete_preview"] = new() { ["name"] = "none" },
            ["oadm_backups_download"] = new() { ["name"] = "none" },
            ["oadm_backups_restore_preview"] = new() { ["name"] = "none" },
            ["oadm_backups_schedule_preview"] = new() { ["enabled"] = false },
            ["oadm_license_import_preview"] = [],
            ["oadm_license_remove_preview"] = [],
            ["oadm_license_trial_preview"] = [],
            ["oadm_plugins_enable_preview"] = new() { ["plugin_id"] = "none", ["enabled"] = true },
            ["oadm_plugins_license_preview"] = new() { ["plugin_id"] = "none" },
            ["oadm_plugins_license_remove_preview"] = new() { ["plugin_id"] = "none" },
            ["oadm_plugins_optional"] = [],
            ["oadm_plugins_optional_install_preview"] = new() { ["plugin_id"] = "none" },
            ["oadm_plugins_optional_remove_preview"] = new() { ["plugin_id"] = "none" },
            ["oadm_plugins_optional_restart_preview"] = [],
            ["oadm_support_report"] = [],
            ["oadm_update_install_preview"] = [],
        };
        foreach (var run in RunTools)
        {
            calls[run] = new JsonObject { ["handle"] = "ended" };
        }

        return calls;
    }

    private const string Canary = "canary-backup-Pw!9 \"ü\"";

    private static readonly (string PrivateKeyPem, string PublicKey) Key = LicenseFile.CreateKeys();

    private sealed class RecordingRestart : IServerRestart
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void Request() => Interlocked.Increment(ref _requests);
    }

    private sealed class RecordingStarter : IServerHelperStarter
    {
        public ConcurrentQueue<IReadOnlyList<string>> Starts { get; } = new();

        public Task StartAsync(string helper, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Starts.Enqueue(arguments); // never a real helper or installer
            return Task.CompletedTask;
        }
    }

    private sealed record Rig(McpTestServer Server, FakeTimeProvider Clock, RecordingRestart Restart, RecordingStarter Starter, FakeAddOnHashes Hashes) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    private const string Installer = "OADM-4.9.0-win-x64.msi";

    private static async Task<Rig> StartAsync(bool installed = true, string version = "4.8.0")
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var restart = new RecordingRestart();
        var starter = new RecordingStarter();
        var hashes = new FakeAddOnHashes();
        var server = await McpTestServer.StartAsync(accessClock: clock, configureServices: services =>
        {
            services.RemoveAll<ProLicense>();
            services.AddSingleton(sp => new ProLicense(sp.GetRequiredService<ServerSettingsStore>(), () => sp.GetRequiredService<PluginLicenses>().ServerId, publicKeys: [Key.PublicKey]));
            services.RemoveAll<BackupArchive>();
            services.AddSingleton(sp => new BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
            services.RemoveAll<IServerRestart>();
            services.AddSingleton<IServerRestart>(restart);
            services.RemoveAll<AddOnInstallerOptions>();
            services.AddSingleton(sp => new AddOnInstallerOptions(sp.GetRequiredService<OadmPaths>().DataDirectory, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId()));
            services.RemoveAll<IAddOnHashes>();
            services.AddSingleton<IAddOnHashes>(hashes);
            services.RemoveAll<IReleaseFeed>();
            services.AddSingleton<IReleaseFeed>(FakeReleaseFeed.With(Installer, Encoding.UTF8.GetBytes("the msi")));
            services.RemoveAll<IDiskSpace>();
            services.AddSingleton<IDiskSpace>(new Updates.FakeDiskSpace(long.MaxValue));
            services.RemoveAll<IServerHelperStarter>();
            services.AddSingleton<IServerHelperStarter>(starter);
            services.RemoveAll<ServerUpdateEnvironment>();
            services.AddSingleton(sp => new ServerUpdateEnvironment
            {
                ServerVersion = version,
                Platform = new UpdatePlatform(UpdateOs.Windows, System.Runtime.InteropServices.Architecture.X64),
                Installed = installed,
                ConnectionString = sp.GetRequiredService<OadmPaths>().ConnectionString,
                DataDirectory = sp.GetRequiredService<OadmPaths>().DataDirectory,
                MakeHelper = folder => Path.Combine(folder, "helper-test", "Oadm.Server.exe"),
                StatusUrl = () => new Uri("https://127.0.0.1:5080"),
            });
        });
        return new Rig(server, clock, restart, starter, hashes);
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static async Task<IReadOnlyList<Proto.AuditEntry>> AuditAsync(McpTestServer server) =>
        (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;

    private static async Task<HttpResponseMessage> SendAsync(McpTestServer server, HttpMethod method, string key, string url, byte[]? body = null)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(method, new Uri(url).PathAndQuery);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    /// <summary>The user types <paramref name="value"/> into the command of the secret link <paramref name="slot"/> of a preview.</summary>
    private static async Task SetSecretAsync(McpTestServer server, string key, JsonObject preview, string slot, string value)
    {
        var url = preview["secret_links"]![slot]!["url"]!.GetValue<string>();
        using var set = await SendAsync(server, HttpMethod.Put, key, url, Encoding.UTF8.GetBytes(value + "\n"));
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    /// <summary>Sends <paramref name="content"/> through an upload link; its file id.</summary>
    private static async Task<string> UploadAsync(McpTestServer server, string key, string name, byte[] content)
    {
        var link = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = name, ["size"] = content.Length });
        using var put = await SendAsync(server, HttpMethod.Put, key, link["url"]!.GetValue<string>(), content);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return JsonNode.Parse(await put.Content.ReadAsStringAsync())!["file_id"]!.GetValue<string>();
    }

    /// <summary>Fetches the file of a tool answer through a download link.</summary>
    private static async Task<byte[]> FetchAsync(McpTestServer server, string key, JsonObject answer)
    {
        var link = await server.ToolAsync(key, "oadm_files_download_link", new JsonObject { ["file_ref"] = answer["file"]!["file_ref"]!.GetValue<string>() });
        using var get = await SendAsync(server, HttpMethod.Get, key, link["url"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        return await get.Content.ReadAsByteArrayAsync();
    }

    private static Proto.AssistantPreviewCard Card(McpTestServer server, string preview, JsonObject answer)
    {
        var registry = server.Host.Get<ToolRegistry>();
        Assert.True(registry.TryGet(preview, out var tool));
        registry.TryGet(AssistantTools.RunOf(preview), out var run);
        var card = AssistantCards.Build(tool, run, answer["handle"]?.GetValue<string>() ?? "h", answer, JsonSerializer.SerializeToElement(new JsonObject()));
        Assert.NotEmpty(card.Details);
        Assert.False(string.IsNullOrEmpty(card.CountText));
        return card;
    }

    private static async Task AssertOnceAsync(McpTestServer server, string key, string run, string handle)
    {
        var again = await server.CallToolAsync(key, run, new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>(), run);
        Assert.Contains(AreaListing.PreviewEnded, Text(again), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAdministratorsListTheseToolsAndAnOperatorIsRefused()
    {
        await using var rig = await StartAsync();
        var admin = await rig.Server.CreateKeyAsync();
        var names = await rig.Server.ToolNamesAsync(admin);
        Assert.All(ToolNames, n => Assert.Contains(n, names));

        var operatorKey = await rig.Server.CreateKeyAsync(roleId: BuiltInRoles.OperatorId.ToString());
        var theirs = await rig.Server.ToolNamesAsync(operatorKey);
        Assert.All(ToolNames.Except(["oadm_plugins_optional"]), n => Assert.DoesNotContain(n, theirs)); // the list of optional plugins is every user's
        var refused = await rig.Server.CallToolAsync(operatorKey, "oadm_backups_create_preview");
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Only administrators", Text(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLicenseKeyComesOnlyThroughTheSecretChannelAndTheLicenseIsImportedRemovedAndTheTrialStartedByTheRuns()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync(name: "Lic-bot");
        var answers = new StringBuilder();

        var preview = await server.ToolAsync(key, "oadm_license_import_preview");
        answers.Append(preview.ToJsonString());
        Assert.Equal("None", preview["status"]!.GetValue<string>());
        Assert.Equal(["license_key"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.StartsWith(AdminTools.ActivatePage, preview["activate_url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("1 license", Card(server, "oadm_license_import_preview", preview).CountText);
        var handle = preview["handle"]!.GetValue<string>();

        var early = await server.CallToolAsync(key, "oadm_license_import_run", new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the license key.", Text(early), StringComparison.Ordinal);
        Assert.Equal(Proto.ProLicenseStatus.ProLicenseNone, (await new Proto.LicenseService.LicenseServiceClient(server.Host.Invoker).GetLicenseAsync(new Proto.Empty())).Status);

        var serverId = (await new Proto.LicenseService.LicenseServiceClient(server.Host.Invoker).GetLicenseAsync(new Proto.Empty())).ServerId;
        var license = ProLicenseFile.Sign(Key.PrivateKeyPem, serverId, "ACME GmbH", "Up to 500 devices", 500, DateOnly.FromDateTime(DateTime.Today), new DateOnly(2099, 12, 31), false);
        await SetSecretAsync(server, key, preview, "license_key", license);
        var run = await server.ToolAsync(key, "oadm_license_import_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.Equal(("Active", "Up to 500 devices"), (run["status"]!.GetValue<string>(), run["plan"]!.GetValue<string>()));
        await AssertOnceAsync(server, key, "oadm_license_import_run", handle);
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.ProLicenseImported && e.UserName == AccessKeyStore.AgentNameOf("Lic-bot"));

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        var remove = await server.ToolAsync(key, "oadm_license_remove_preview");
        Assert.True(Card(server, "oadm_license_remove_preview", remove).Destructive);
        Assert.True(remove["has_key"]!.GetValue<bool>());
        Assert.Equal(Proto.ProLicenseStatus.ProLicenseActive, (await new Proto.LicenseService.LicenseServiceClient(server.Host.Invoker).GetLicenseAsync(new Proto.Empty())).Status);
        var removed = await server.ToolAsync(key, "oadm_license_remove_run", new JsonObject { ["handle"] = remove["handle"]!.GetValue<string>() });
        Assert.Equal("None", removed["status"]!.GetValue<string>());
        var nothing = await server.ToolAsync(key, "oadm_license_remove_preview");
        Assert.Null(nothing["handle"]);
        Assert.Contains(AdminTools.NoKey, nothing["warnings"]!.ToJsonString(), StringComparison.Ordinal);

        var trial = await server.ToolAsync(key, "oadm_license_trial_preview");
        Assert.True(trial["trial_available"]!.GetValue<bool>());
        var started = await server.ToolAsync(key, "oadm_license_trial_run", new JsonObject { ["handle"] = trial["handle"]!.GetValue<string>() });
        Assert.True(started["trial"]!.GetValue<bool>());
        var again = await server.ToolAsync(key, "oadm_license_trial_preview");
        Assert.Null(again["handle"]); // once per server

        // The key is in no answer, audit entry or log line.
        answers.Append((await AuditAsync(server)).Select(e => e.Detail + e.Target).Aggregate(string.Empty, string.Concat));
        Assert.DoesNotContain(license, answers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(license[..40], string.Concat(McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PluginsTurnOnAndOffOnlyWithTheRunAndAPartOfOadmOrAFreePluginIsRefused()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync();
        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(
            new Oadm.Plugins.Restart.RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System),
            new PluginOrigin("oadm.restart", "1.0.0", null) { DisplayName = "Restart" }));
        var packages = await server.Host.Plugins.ListPackagesAsync(new Proto.Empty());
        var free = packages.Packages.Single(p => p.Id == "oadm.restart");
        Assert.True(free.Enabled);

        var preview = await server.ToolAsync(key, "oadm_plugins_enable_preview", new JsonObject { ["plugin_id"] = free.Id, ["enabled"] = false });
        Assert.True(Card(server, "oadm_plugins_enable_preview", preview).Destructive);
        Assert.True((await server.Host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.Single(p => p.Id == free.Id).Enabled); // nothing written
        var off = await server.ToolAsync(key, "oadm_plugins_enable_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.False(off["on"]!.GetValue<bool>());
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.PluginTurnedOff && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
        await AssertOnceAsync(server, key, "oadm_plugins_enable_run", preview["handle"]!.GetValue<string>());

        var same = await server.ToolAsync(key, "oadm_plugins_enable_preview", new JsonObject { ["plugin_id"] = free.Id, ["enabled"] = false });
        Assert.Null(same["handle"]);

        Assert.True(server.Host.Get<PluginRegistry>().RegisterTaskPlugin(new ResultFileTask(), new PluginOrigin(ResultFileTask.PluginId, "1.0.0", null) { AlwaysOn = true }));
        var pki = await server.CallToolAsync(key, "oadm_plugins_enable_preview", new JsonObject { ["plugin_id"] = ResultFileTask.PluginId, ["enabled"] = false });
        Assert.True(pki["isError"]!.GetValue<bool>());
        Assert.Contains(AdminTools.AlwaysOn, Text(pki), StringComparison.Ordinal);
        var unknown = await server.CallToolAsync(key, "oadm_plugins_enable_preview", new JsonObject { ["plugin_id"] = "oadm.none", ["enabled"] = true });
        Assert.Contains(AdminTools.UnknownPlugin, Text(unknown), StringComparison.Ordinal);
        var license = await server.CallToolAsync(key, "oadm_plugins_license_preview", new JsonObject { ["plugin_id"] = free.Id });
        Assert.Contains(AdminTools.NoLicenseNeeded, Text(license), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOptionalPluginInstallsOnlyFromTheGenuineZipOfAnUploadLinkAndIsRemovedByTheRun()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var zip = AddOnRig.Zip();
        rig.Hashes.Allow(zip);
        var key = await server.CreateKeyAsync();
        const string Id = AddOnCatalog.TextRecognitionId;
        var fileName = AddOnCatalog.ReleaseFileName(Id, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId());

        var listed = await server.ToolAsync(key, "oadm_plugins_optional");
        Assert.Equal("not_installed", listed["plugins"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == Id)!["state"]!.GetValue<string>());
        var noFile = await server.CallToolAsync(key, "oadm_plugins_optional_install_preview", new JsonObject { ["plugin_id"] = Id });
        Assert.Contains(AdminTools.SendZip, Text(noFile), StringComparison.Ordinal);

        // A changed ZIP: the preview shows its SHA-256, the run is refused by the server's check and nothing is installed.
        var tampered = AddOnRig.Zip(extraBytes: 10);
        var badId = await UploadAsync(server, key, fileName, tampered);
        var bad = await server.ToolAsync(key, "oadm_plugins_optional_install_preview", new JsonObject { ["plugin_id"] = Id, ["file_id"] = badId });
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(tampered)), bad["file"]!["sha256"]!.GetValue<string>());
        var refused = await server.CallToolAsync(key, "oadm_plugins_optional_install_run", new JsonObject { ["handle"] = bad["handle"]!.GetValue<string>() });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains(AddOnTexts.WrongFile, Text(refused), StringComparison.Ordinal);
        Assert.False(server.Host.Get<PluginRegistry>().HasPackage(Id));

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        var fileId = await UploadAsync(server, key, fileName, zip);
        var preview = await server.ToolAsync(key, "oadm_plugins_optional_install_preview", new JsonObject { ["plugin_id"] = Id, ["file_id"] = fileId });
        Assert.Equal("1 plugin", Card(server, "oadm_plugins_optional_install_preview", preview).CountText);
        Assert.False(server.Host.Get<PluginRegistry>().HasPackage(Id)); // nothing installed by the preview
        var run = await server.ToolAsync(key, "oadm_plugins_optional_install_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal("installed", run["state"]!.GetValue<string>());
        Assert.True(server.Host.Get<PluginRegistry>().HasPackage(Id));
        Assert.False(server.Host.Get<PluginRegistry>().IsEnabled(Id)); // starts off
        Assert.Contains(await AuditAsync(server), e => e.Action == "Installed optional plugin" && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));

        var remove = await server.ToolAsync(key, "oadm_plugins_optional_remove_preview", new JsonObject { ["plugin_id"] = Id });
        Assert.True(Card(server, "oadm_plugins_optional_remove_preview", remove).Destructive);
        var removed = await server.ToolAsync(key, "oadm_plugins_optional_remove_run", new JsonObject { ["handle"] = remove["handle"]!.GetValue<string>() });
        Assert.Equal("removed", removed["state"]!.GetValue<string>());

        var restart = await server.ToolAsync(key, "oadm_plugins_optional_restart_preview");
        Assert.Null(restart["handle"]); // nothing waits
        Assert.Contains(AddOnTexts.NothingWaits, restart["warnings"]!.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(0, rig.Restart.Requests);
    }

    [Fact]
    public async Task ABackupIsMadeWithThePasswordTypedTwiceAndFetchedAsAFileAndThePasswordIsNeverInAnAnswerOrTheAudit()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync(name: "Backup-bot");
        var answers = new StringBuilder();

        var preview = await server.ToolAsync(key, "oadm_backups_create_preview");
        answers.Append(preview.ToJsonString());
        Assert.Equal(["password", "password_again"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("1 backup", Card(server, "oadm_backups_create_preview", preview).CountText);
        Assert.DoesNotContain(await AuditAsync(server), e => e.Action == AuditActions.BackupCreated); // nothing made

        // Two different passwords: the run is refused and makes nothing.
        await SetSecretAsync(server, key, preview, "password", Canary);
        await SetSecretAsync(server, key, preview, "password_again", Canary + "x");
        var mismatch = await server.CallToolAsync(key, "oadm_backups_create_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Append(mismatch.ToJsonString());
        Assert.Contains(BackupTools.NoMatch, Text(mismatch), StringComparison.Ordinal);

        var second = await server.ToolAsync(key, "oadm_backups_create_preview");
        await SetSecretAsync(server, key, second, "password", Canary);
        await SetSecretAsync(server, key, second, "password_again", Canary);
        var made = await server.ToolAsync(key, "oadm_backups_create_run", new JsonObject { ["handle"] = second["handle"]!.GetValue<string>() });
        answers.Append(made.ToJsonString());
        var bytes = await FetchAsync(server, key, made);
        Assert.Equal("OADMBACKUP", Encoding.ASCII.GetString(bytes, 0, 10));
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.BackupCreated && e.UserName == AccessKeyStore.AgentNameOf("Backup-bot"));

        // The file is the backup of that password: it checks with it.
        var inspected = await server.Host.Get<BackupManager>().InspectAsync(_ => Task.FromResult<Stream>(new MemoryStream(bytes)), Canary, CancellationToken.None);
        Assert.False(inspected.WrongPassword);

        answers.Append((await AuditAsync(server)).Select(e => e.Detail + e.Target).Aggregate(string.Empty, string.Concat));
        Assert.DoesNotContain(Canary, answers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, string.Concat(McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScheduleChangesOnlyWithTheRunAndKeptBackupsAreListedDownloadedAndDeleted()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync();

        var on = await server.ToolAsync(key, "oadm_backups_schedule_preview", new JsonObject { ["enabled"] = true, ["time_of_day"] = "03:30", ["keep"] = 3 });
        Assert.True(on["new_password"]!.GetValue<bool>()); // none stored yet
        Assert.False((await server.Host.Get<BackupManager>().GetScheduleAsync(CancellationToken.None)).Enabled); // nothing written
        var bad = await server.CallToolAsync(key, "oadm_backups_schedule_preview", new JsonObject { ["enabled"] = true, ["time_of_day"] = "25:00" });
        Assert.Contains("Enter a time like 02:00.", Text(bad), StringComparison.Ordinal);
        await SetSecretAsync(server, key, on, "password", Canary);
        await SetSecretAsync(server, key, on, "password_again", Canary);
        var saved = await server.ToolAsync(key, "oadm_backups_schedule_run", new JsonObject { ["handle"] = on["handle"]!.GetValue<string>() });
        Assert.Equal((true, "03:30", 3, true), (saved["enabled"]!.GetValue<bool>(), saved["time_of_day"]!.GetValue<string>(), saved["keep"]!.GetValue<int>(), saved["has_password"]!.GetValue<bool>()));

        // An automatic backup on the server: listed, downloaded through a link (audited by the backup service), deleted.
        await server.Host.Get<BackupManager>().RunAutomaticAsync(CancellationToken.None);
        var list = await server.ToolAsync(key, "oadm_backups");
        var name = list["backups"]![0]!["name"]!.GetValue<string>();
        Assert.Equal("automatic", list["backups"]![0]!["kind"]!.GetValue<string>());
        var download = await server.ToolAsync(key, "oadm_backups_download", new JsonObject { ["name"] = name });
        Assert.Equal("OADMBACKUP", Encoding.ASCII.GetString((await FetchAsync(server, key, download)).AsSpan(0, 10)));
        Assert.Contains(await AuditAsync(server), e => e.Action == AuditActions.BackupDownloaded && e.Target == name);

        var delete = await server.ToolAsync(key, "oadm_backups_delete_preview", new JsonObject { ["name"] = name });
        Assert.True(Card(server, "oadm_backups_delete_preview", delete).Destructive);
        Assert.NotNull(server.Host.Get<BackupManager>().PathOf(name));
        var deleted = await server.ToolAsync(key, "oadm_backups_delete_run", new JsonObject { ["handle"] = delete["handle"]!.GetValue<string>() });
        Assert.Equal(0, deleted["total"]!.GetValue<int>());
        Assert.Null(server.Host.Get<BackupManager>().PathOf(name));
        var gone = await server.CallToolAsync(key, "oadm_backups_download", new JsonObject { ["name"] = name });
        Assert.Contains(BackupTools.NoSuchBackup, Text(gone), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoreWarnsLoudlyChecksThePasswordFirstAndRestartsOnlyWithTheRun()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync();
        using var file = new MemoryStream();
        await server.Host.Get<BackupManager>().CreateAsync(file, Canary, CancellationToken.None);
        var fileId = await UploadAsync(server, key, "site-a.oadmbackup", file.ToArray());

        var both = await server.CallToolAsync(key, "oadm_backups_restore_preview", new JsonObject { ["file_id"] = fileId, ["name"] = "x" });
        Assert.Contains(BackupTools.OneSource, both.ToJsonString(), StringComparison.Ordinal);

        var preview = await server.ToolAsync(key, "oadm_backups_restore_preview", new JsonObject { ["file_id"] = fileId });
        var warnings = preview["warnings"]!.ToJsonString();
        Assert.Contains("EVERYTHING on this server is replaced", warnings, StringComparison.Ordinal);
        Assert.Contains("access key ends", warnings, StringComparison.Ordinal);
        Assert.Contains("loses its connection", warnings, StringComparison.Ordinal);
        var card = Card(server, "oadm_backups_restore_preview", preview);
        Assert.True(card.Destructive);
        Assert.Equal("1 backup", card.CountText);

        await SetSecretAsync(server, key, preview, "password", "wrong-password-1");
        var wrong = await server.CallToolAsync(key, "oadm_backups_restore_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Contains(BackupTools.WrongPassword, Text(wrong), StringComparison.Ordinal);
        Assert.Equal(0, rig.Restart.Requests);

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        var again = await server.ToolAsync(key, "oadm_backups_restore_preview", new JsonObject { ["file_id"] = fileId });
        await SetSecretAsync(server, key, again, "password", Canary);
        var run = await server.ToolAsync(key, "oadm_backups_restore_run", new JsonObject { ["handle"] = again["handle"]!.GetValue<string>() });
        Assert.Contains("restarts", run["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.NotEmpty(run["safety_backup"]!.GetValue<string>());
        Assert.Equal(1, rig.Restart.Requests);
        Assert.DoesNotContain(Canary, run.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSupportReportIsAFileOfALink()
    {
        await using var rig = await StartAsync();
        var key = await rig.Server.CreateKeyAsync();

        var report = await rig.Server.ToolAsync(key, "oadm_support_report");
        Assert.EndsWith(".zip", report["file"]!["name"]!.GetValue<string>(), StringComparison.Ordinal);
        var bytes = await FetchAsync(rig.Server, key, report);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "summary.txt");
        Assert.Contains(await AuditAsync(rig.Server), e => e.Action == AuditActions.SupportReportCreated && e.UserName.StartsWith("MCP ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InstallNowIsAPreviewAndARunWithTheRulesOfSelfUpdateAndNoRealInstaller()
    {
        await using var rig = await StartAsync();
        var server = rig.Server;
        var key = await server.CreateKeyAsync();

        var preview = await server.ToolAsync(key, "oadm_update_install_preview");
        Assert.Equal("4.9.0", preview["version"]!.GetValue<string>());
        Assert.Contains("loses its connection", preview["warnings"]!.ToJsonString(), StringComparison.Ordinal);
        var card = Card(server, "oadm_update_install_preview", preview);
        Assert.True(card.Destructive);
        Assert.Empty(rig.Starter.Starts); // the preview starts nothing
        Assert.Null(server.Host.Get<UpdateGate>().UpdatingTo);

        var run = await server.ToolAsync(key, "oadm_update_install_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal("4.9.0", run["to_version"]!.GetValue<string>());
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => !rig.Starter.Starts.IsEmpty, "the fake helper was started");
        // Every other call is closed now (the update runs); the audit log is read from the store.
        var started = Assert.Single((await server.Host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None)).Entries, e => e.Action == AuditActions.ServerUpdateStarted);
        Assert.Equal(("OADM 4.9.0", "From 4.8.0"), (started.Target, started.Detail));
        Assert.StartsWith("MCP ", started.UserName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "4.8.0", UpdateTexts.NotInstalled)]
    [InlineData(true, "4.9.0", HostActionTools.UpToDate)]
    [InlineData(true, "0.1.0-dev", UpdateTexts.DevelopmentBuild)]
    public async Task InstallNowIsNotOfferedForAServerThatCannotTakeIt(bool installed, string version, string reason)
    {
        await using var rig = await StartAsync(installed, version);
        var key = await rig.Server.CreateKeyAsync();

        var preview = await rig.Server.ToolAsync(key, "oadm_update_install_preview");

        Assert.Null(preview["handle"]);
        Assert.Contains(reason, preview["warnings"]!.ToJsonString(), StringComparison.Ordinal);
    }
}
