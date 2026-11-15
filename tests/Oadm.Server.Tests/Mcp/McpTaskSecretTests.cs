using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The secrets of a task run (docs/spec/mcp.md "Secrets", "Task secrets"): a <see cref="SecretAttribute"/> property is left out
/// of the task's input schema, the preview declares a slot per <see cref="ITaskPlugin.SecretsOf"/> entry, a value the person
/// enters is checked at once with the plugin's rules, and the run gets it in its payload once; it never reaches an answer, the
/// audit log, the server log, the task table or the data folder.
/// </summary>
public sealed class McpTaskSecretTests
{
    private const string Canary = "canary-task-secret-9Zp!q";

    private static async Task<(McpTestServer Server, SecretTask Task)> StartAsync()
    {
        var server = await McpTestServer.StartAsync();
        var task = new SecretTask();
        var registry = server.Host.Get<PluginRegistry>();
        Assert.True(registry.RegisterTaskPlugin(task, new PluginOrigin(SecretTask.PluginId, "1.0.0", null)));
        await server.Host.Get<RoleStore>().SyncCatalogAsync(PermissionCatalog.Build(registry), CancellationToken.None);
        return (server, task);
    }

    private static async Task<HttpResponseMessage> PutAsync(McpTestServer server, string key, string url, string value)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var response = await client.SendAsync(request);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [Fact]
    public void ASecretPropertyIsLeftOutOfTheSchemaAndIsASlot()
    {
        var schema = TaskPayloadSchemas.Of(new SecretTask())!;
        Assert.Equal(["user"], schema["properties"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["user"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        var slot = Assert.Single(((ITaskPlugin)new SecretTask()).SecretsOf("""{"user":"joe"}"""));
        Assert.Equal(new TaskSecret("password", "passphrase", "password"), slot);
        Assert.Equal("pkcs12_password", TaskSecrets.SlotName("pkcs12Password"));
    }

    [Fact]
    public void TheFieldsOfASecretAreSetInTheirPlaceOnly()
    {
        Assert.Equal("""{"a":1,"password":"x"}""", TaskSecretPaths.With("""{"a":1}""", [("password", "x")]));
        Assert.Equal("""{"settings":[{"name":"n","value":""},{"name":"p","value":"s"}]}""",
            TaskSecretPaths.With("""{"settings":[{"name":"n","value":""},{"name":"p","value":""}]}""", [("settings[1].value", "s")]));
        Assert.Equal("""{"tls":{"key":"k"}}""", TaskSecretPaths.With("{}", [("tls.key", "k")]));
        Assert.Throws<ToolException>(() => TaskSecretPaths.With("""{"settings":[]}""", [("settings[3].value", "s")])); // never lost quietly
        Assert.Equal("p", TaskSecretPaths.Get(JsonNode.Parse("""{"settings":[{"name":"p"}]}"""), "settings[0].name")!.GetValue<string>());
        Assert.Null(TaskSecretPaths.Get(JsonNode.Parse("{}"), "settings[0].name"));
        Assert.True(TaskGrpcService.IsField("settings[1].value", "settings[1]"));
        Assert.False(TaskGrpcService.IsField("settings[10].value", "settings[1]"));
    }

    [Fact]
    public async Task ATaskSecretIsCheckedWhenEnteredReachesTheRunOnceAndNothingElse()
    {
        var (server, task) = await StartAsync();
        await using var _ = server;
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.31.0.1", 4501);
        var key = await server.CreateKeyAsync(name: "Lobby-bot");
        var answers = new StringBuilder();

        // The catalog offers it, its schema without the secret; an agent cannot send one.
        var catalog = await server.ToolAsync(key, "oadm_tasks_catalog", new JsonObject { ["task"] = SecretTask.PluginId });
        Assert.DoesNotContain("password", catalog["tasks"]![0]!["input_schema"]!.ToJsonString(), StringComparison.Ordinal);
        var sent = await server.CallToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = SecretTask.PluginId,
            ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) },
            ["input"] = new JsonObject { ["user"] = "joe", ["password"] = Canary[..5] },
        });
        Assert.True(sent["isError"]!.GetValue<bool>());

        var preview = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = SecretTask.PluginId,
            ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) },
            ["input"] = new JsonObject { ["user"] = "joe" },
        });
        answers.Append(preview.ToJsonString());
        Assert.Equal("password", Assert.Single(preview["needs_secrets"]!.AsArray())!.GetValue<string>());
        Assert.Contains("Passphrase: ", preview["secret_links"]!["password"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var handle = preview["handle"]!.GetValue<string>();
        var url = preview["secret_links"]!["password"]!["url"]!.GetValue<string>();

        var early = await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.True(early["isError"]!.GetValue<bool>());
        Assert.Contains("Enter the passphrase.", early.ToJsonString(), StringComparison.Ordinal);

        // The plugin's own rule refuses a short value at once; the link stays for a better one.
        using (var shortValue = await PutAsync(server, key, url, "short"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, shortValue.StatusCode);
            Assert.Contains(SecretTask.TooShort, await shortValue.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var set = await PutAsync(server, key, url, Canary))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            answers.Append(await set.Content.ReadAsStringAsync());
        }

        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        answers.Append(run.ToJsonString());
        Assert.Equal(1, run["tasks"]!.GetValue<int>());
        await Eventually.TrueAsync(() => task.Received.Count == 1, "the task ran");
        Assert.Equal(Canary, Assert.Single(task.Received));
        var batch = run["batch"]!.GetValue<string>();
        await Eventually.TrueAsync(async () => (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks
            .Any(t => t.BatchId == batch && t.State == Proto.TaskState.Done), "the task ended");
        var listed = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = batch });
        answers.Append(listed.ToJsonString());
        var again = await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>()); // once

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.SecretSet && e.Target == "passphrase" && e.Detail == "For Run a task");
        Assert.Contains(audit, e => e.Action == AuditActions.TaskRun && e.UserName == AccessKeyStore.AgentNameOf("Lobby-bot"));
        await SecretScan.AssertNowhereAsync(server.Host, Canary, answers.ToString());
    }

    /// <summary>A task with a passphrase: at least 8 characters (the dialog's rule), the value it got kept for the test.</summary>
    internal sealed class SecretTask : ITaskPlugin
    {
        public const string PluginId = "test.secret-task";
        public const string TooShort = "Use at least 8 characters.";

        public List<string> Received { get; } = [];

        public string Id => PluginId;

        public string DisplayName => "Set a passphrase";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public Type? PayloadType => typeof(Payload);

        public bool CanRun(IDeviceInfo device) => true;

        public IReadOnlyList<PayloadError> ValidatePayload(string? payloadJson, ICallContext caller)
        {
            var payload = JsonSerializer.Deserialize<Payload>(payloadJson ?? "{}", PluginJson.CreateOptions());
            return payload?.Password is not { Length: >= 8 } ? [new PayloadError("password", payload?.Password is { Length: > 0 } ? TooShort : "Enter the passphrase.")] : [];
        }

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            var payload = JsonSerializer.Deserialize<Payload>(payloadJson!, PluginJson.CreateOptions())!;
            lock (Received)
            {
                Received.Add(payload.Password!);
            }

            ctx.Log(TaskLogLevel.Info, "Set the passphrase of " + payload.User + ".");
            return Task.CompletedTask;
        }

        public sealed record Payload(
            [property: System.ComponentModel.Description("The user."), System.ComponentModel.DataAnnotations.MaxLength(32)] string User,
            [property: Secret(Label = "passphrase")] string? Password = null);
    }
}
