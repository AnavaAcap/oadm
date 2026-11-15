using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.Plugins;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// A plugin change with a secret and a file (SDK <c>PluginTool.Secrets</c>, <c>ICallContext.Uploads</c>, docs/spec/mcp.md
/// "SDK"): the host declares the secret on the preview's handle, the person fills it through the secret link, the run gets it
/// once and no answer, audit entry or log line holds it; a <c>file_id</c> is read from the caller's own uploads only.
/// </summary>
public sealed class McpPluginSecretTests
{
    private const string Code = "canary-lock-7d21";

    private static async Task<(McpTestServer Server, LocksPlugin Plugin)> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var registry = server.Host.Get<PluginRegistry>();
        var plugin = new LocksPlugin();
        registry.RegisterCorePlugin(plugin, new PluginOrigin(plugin.Id, "1.0.0", null) { DisplayName = plugin.DisplayName, MinSdkVersion = SdkInfo.Version });
        await server.Host.Get<PluginActivation>().LoadAsync(CancellationToken.None);
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        await server.Host.Get<CorePluginHost>().StartAllAsync(CancellationToken.None);
        return (server, plugin);
    }

    private static async Task<HttpResponseMessage> SendAsync(McpTestServer server, string key, string url, byte[] body)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static string Text(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task TheRunGetsTheSecretOnceThroughTheLinkAndNoAnswerAuditOrLogHoldsIt()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var key = await server.CreateKeyAsync();

        // A plan that names no secret: none is asked for.
        var keep = await server.ToolAsync(key, "oadm_locks_lock_save_preview", new JsonObject { ["name"] = "Gate", ["keep_code"] = true });
        Assert.Empty(keep["needs_secrets"]!.AsArray());
        Assert.False(keep.ContainsKey(PluginToolSecret.NeedsProperty) && keep[PluginToolSecret.NeedsProperty] is JsonArray { Count: > 0 });

        var preview = await server.ToolAsync(key, "oadm_locks_lock_save_preview", new JsonObject { ["name"] = "Door" });
        Assert.Equal(["code"], preview["needs_secrets"]!.AsArray().Select(n => n!.GetValue<string>()));
        var link = preview["secret_links"]!["code"]!["url"]!.GetValue<string>();
        var handle = preview["handle"]!.GetValue<string>();
        Assert.Null(plugin.LastCode); // the preview never has it

        var missing = await server.CallToolAsync(key, "oadm_locks_lock_save_run", new JsonObject { ["handle"] = handle });
        Assert.True(missing["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the lock code.", Text(missing), StringComparison.Ordinal);

        using (var set = await SendAsync(server, key, link, Encoding.UTF8.GetBytes(Code)))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        var run = await server.ToolAsync(key, "oadm_locks_lock_save_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(Code.Length, run["code_length"]!.GetValue<int>());
        Assert.Equal(Code, plugin.LastCode);
        var again = await server.CallToolAsync(key, "oadm_locks_lock_save_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries.Select(e => e.Target + "|" + e.Detail).ToList();
        Assert.Contains(audit, e => e.Contains("lock", StringComparison.OrdinalIgnoreCase)); // the run's audited call and "Set secret"
        Assert.DoesNotContain(audit, e => e.Contains(Code, StringComparison.Ordinal));
        Assert.DoesNotContain(Code, preview.ToJsonString() + run.ToJsonString() + missing.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Code, McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileIdIsReadFromTheCallersOwnUploadsOnly()
    {
        var (server, plugin) = await StartAsync();
        await using var _ = server;
        var owner = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "other");
        var content = Encoding.UTF8.GetBytes("lock configuration");

        var upload = await server.ToolAsync(owner, "oadm_files_upload_link", new JsonObject { ["name"] = "lock.cfg", ["size"] = content.Length });
        string fileId;
        using (var put = await SendAsync(server, owner, upload["url"]!.GetValue<string>(), content))
        {
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            fileId = JsonNode.Parse(await put.Content.ReadAsStringAsync())!["file_id"]!.GetValue<string>();
        }

        var preview = await server.ToolAsync(owner, "oadm_locks_lock_save_preview", new JsonObject { ["name"] = "Door", ["file_id"] = fileId, ["keep_code"] = true });
        Assert.Equal("lock.cfg", preview["file_name"]!.GetValue<string>());
        var run = await server.ToolAsync(owner, "oadm_locks_lock_save_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(content.Length, run["file_bytes"]!.GetValue<int>());

        // Another key's upload answers like an unknown id.
        var foreign = await server.CallToolAsync(other, "oadm_locks_lock_save_preview", new JsonObject { ["name"] = "Door", ["file_id"] = fileId, ["keep_code"] = true });
        Assert.True(foreign["isError"]!.GetValue<bool>());
        Assert.Contains(LocksPlugin.NoFile, Text(foreign), StringComparison.Ordinal);
        Assert.Equal(1, plugin.Saves);
    }

    /// <summary>A plugin whose page saves a lock with a code (a secret) and an optional configuration file.</summary>
    private sealed class LocksPlugin : ICorePlugin
    {
        public const string NoFile = "No uploaded file has this id.";

        private static readonly PluginPageTools<LocksPlugin> Set = new PluginPageTools<LocksPlugin>()
            .Change<SaveArgs, SavePlan, Saved>(
                new("lock_save", "Save lock", "Saves a lock with its code.") { Secrets = [new("code", "lock code", 64)] },
                "save",
                (_, caller, input, ct) => CheckAsync(caller, input, ct),
                async (plugin, caller, input, plan, ct) =>
                {
                    var bytes = 0;
                    if (plan.FileName is not null)
                    {
                        await using var stream = await caller.Uploads.OpenReadAsync(input.Args.FileId!, ct);
                        bytes = (int)stream.Length;
                    }

                    plugin.LastCode = input.Secret("code");
                    plugin.Saves++;
                    return new Saved(plugin.LastCode?.Length ?? 0, bytes);
                });

        public string? LastCode { get; private set; }

        public int Saves { get; private set; }

        public string Id => "tests.locks";

        public string DisplayName => "Locks";

        public string? IconKey => null;

        public string? ToolArea => "locks";

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [];

        public IReadOnlyList<PluginTool> Tools => Set.Tools;

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => throw new InvalidOperationException("old overload");

        public UserRole RequiredRole(string method) => UserRole.Admin;

        public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method);

        public string? AuditDetail(string method, string? payloadJson) => "Saved lock";

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct) =>
            Set.Handles(method) ? await Set.InvokeAsync(this, caller, method, payloadJson, ct) : throw new ArgumentException("Unknown method.", nameof(method));

        private static async Task<SavePlan> CheckAsync(ICallContext caller, PluginToolInput<SaveArgs> input, CancellationToken ct)
        {
            string? fileName = null;
            if (input.Args.FileId is { } id)
            {
                fileName = (await caller.Uploads.FindAsync(id, ct))?.Name ?? throw new PluginToolInputException("file_id", NoFile);
            }

            if (input.IsRun && input.Args.KeepCode != true && input.Secret("code") is null)
            {
                throw new PluginToolInputException("code", "Enter the lock code.");
            }

            return new SavePlan(input.Args.Name, fileName, input.Args.KeepCode == true ? [] : ["code"]);
        }

        internal sealed record SaveArgs(
            [property: Description("The lock's name."), MaxLength(40)] string Name,
            [property: Description("An uploaded configuration file.")] [property: MaxLength(64)] string? FileId = null,
            [property: Description("Keep the stored code.")] bool? KeepCode = null);

        internal sealed record SavePlan(
            [property: Description("The lock.")] string Name,
            [property: Description("The file's name.")] string? FileName,
            [property: Description("The secrets the run needs.")] IReadOnlyList<string> NeedsSecrets) : PluginToolPlan(1, "lock");

        internal sealed record Saved([property: Description("The code's length.")] int CodeLength, [property: Description("The file's size.")] int FileBytes);
    }
}
