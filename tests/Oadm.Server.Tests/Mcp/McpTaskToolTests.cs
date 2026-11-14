using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Plugins.DateAndTime;
using Oadm.Plugins.Restart;
using Oadm.Plugins.Users;
using Oadm.Server.Mcp;
using Oadm.Server.Tasks;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The task tools of stage 2 (docs/spec/mcp.md "Tasks", "Files", rule 2 "No change without a preview"): the catalog, the
/// preview that writes nothing and answers a handle, the run that takes only the handle, the plugin's own payload check
/// (also in <c>TaskService.Run</c>), tasks with a secret never offered, upload links, and Read only keys that change nothing.
/// </summary>
public sealed class McpTaskToolTests
{
    private static async Task<McpTestServer> StartAsync(TimeProvider? accessClock = null)
    {
        var server = await McpTestServer.StartAsync(accessClock: accessClock);
        var plugins = server.Host.Get<PluginRegistry>();
        Assert.True(plugins.RegisterTaskPlugin(new RestartTaskPlugin(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(2), TimeProvider.System), new PluginOrigin("oadm.restart", "1.0.0", null)));
        Assert.True(plugins.RegisterTaskPlugin(new DateTimeTaskPlugin(), new PluginOrigin("oadm.datetime", "1.0.0", null)));
        Assert.True(plugins.RegisterTaskPlugin(new UsersTaskPlugin(), new PluginOrigin("oadm.users", "1.0.0", null)));
        return server;
    }

    [Fact]
    public async Task TheCatalogOffersOnlyTasksWithASchemaAndNoSecretAndCountsTheFilter()
    {
        await using var server = await StartAsync();
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.12.0.1", 4301);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.12.0.2", 4302, status: Oadm.Sdk.Devices.DeviceStatus.Unreachable);
        var key = await server.CreateKeyAsync();

        var catalog = await server.ToolAsync(key, "oadm_tasks_catalog", new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.12.0." } });
        var tasks = catalog["tasks"]!.AsArray().Select(t => t!.AsObject()).ToDictionary(t => t["task"]!.GetValue<string>());
        Assert.Contains(RestartTaskPlugin.PluginId, tasks.Keys);
        Assert.Contains(DateTimeTaskPlugin.PluginId, tasks.Keys);
        Assert.DoesNotContain(UsersTaskPlugin.PluginId, tasks.Keys); // its password is a secret (rule 4)
        var restart = tasks[RestartTaskPlugin.PluginId];
        Assert.Equal(1, restart["can_run"]!.GetValue<int>());
        Assert.Equal(1, Assert.Single(restart["cannot_run"]!.AsArray())!["count"]!.GetValue<int>());
        var schema = tasks[DateTimeTaskPlugin.PluginId]["input_schema"]!.AsObject();
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(["Ntp", "ServerTime", "Manual"], schema["properties"]!["mode"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(5, schema["properties"]!["ntp"]!["properties"]!["servers"]!["maxItems"]!.GetValue<int>());

        var users = await server.CallToolAsync(key, "oadm_tasks_catalog", new JsonObject { ["task"] = UsersTaskPlugin.PluginId });
        Assert.True(users["isError"]!.GetValue<bool>());
        Assert.Contains(TaskRunTools.NotOffered, users["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Null(TaskPayloadSchemas.Of(new UsersTaskPlugin()));
    }

    [Fact]
    public async Task APreviewWritesNothingAndTheRunTakesOnlyItsHandleOnce()
    {
        await using var server = await StartAsync();
        var ok = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.13.0.1", 4401);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.13.0.2", 4402, status: Oadm.Sdk.Devices.DeviceStatus.Unreachable);
        var key = await server.CreateKeyAsync();
        var tasksBefore = (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count;

        var preview = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = RestartTaskPlugin.PluginId,
            ["filter"] = new JsonObject { ["search"] = "10.13.0." },
            ["input"] = new JsonObject(),
        });
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        Assert.Equal(1, preview["will_run"]!.GetValue<int>());
        Assert.Equal(ok.Id.ToString(), Assert.Single(preview["first"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Equal(1, Assert.Single(preview["skipped"]!.AsArray())!["count"]!.GetValue<int>());
        Assert.Equal("Restart device", preview["task_name"]!.GetValue<string>());
        Assert.Equal(tasksBefore, (await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks.Count); // nothing written

        // The handle belongs to its key and runs once.
        var handle = preview["handle"]!.GetValue<string>();
        var other = await server.CreateKeyAsync(name: "other");
        var foreign = await server.CallToolAsync(other, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.True(foreign["isError"]!.GetValue<bool>());
        var run = await server.ToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(1, run["tasks"]!.GetValue<int>());
        var batch = run["batch"]!.GetValue<string>();
        var listed = await server.ToolAsync(key, "oadm_tasks_list", new JsonObject { ["batch"] = batch });
        Assert.Equal(1, listed["total"]!.GetValue<int>());
        Assert.Equal("admin@MCP agent", listed["tasks"]![0]!["owner"]!.GetValue<string>());
        var again = await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());
        Assert.Contains(ToolHandles.Ended, again["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);

        // The run is in the audit log as the key's user.
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.TaskRun && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "agent"));
    }

    [Fact]
    public async Task InvalidInputGivesTheDialogsFieldErrorsAndStartsNoTaskAlsoThroughRun()
    {
        await using var server = await StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.14.0.1", 4501);
        var key = await server.CreateKeyAsync();

        async Task<Dictionary<string, string>> ErrorsAsync(JsonObject input)
        {
            var result = await server.CallToolAsync(key, "oadm_task_preview", new JsonObject { ["task"] = DateTimeTaskPlugin.PluginId, ["filter"] = new JsonObject(), ["input"] = input });
            Assert.True(result["isError"]!.GetValue<bool>());
            var answer = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;
            return answer["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
        }

        // The published schema first: an unknown field, a choice it does not know.
        var schema = await ErrorsAsync(new JsonObject { ["mode"] = "Sundial", ["colour"] = "red" });
        Assert.Equal(ToolInputTexts.Unknown, schema["input.colour"]);
        Assert.StartsWith("Use one of: Ntp, ServerTime, Manual", schema["input.mode"], StringComparison.Ordinal);

        // Then the dialog's rules, at the payload's JSON field.
        var rules = await ErrorsAsync(new JsonObject { ["timeZone"] = "Europe/Vienna", ["mode"] = "Ntp", ["ntp"] = new JsonObject { ["source"] = "Static", ["servers"] = new JsonArray() } });
        Assert.Equal("Enter at least one NTP server.", rules["input.ntp.servers"]);
        Assert.Equal("Select a time zone.", (await ErrorsAsync(new JsonObject { ["timeZone"] = null, ["mode"] = "Manual", ["manualDateTime"] = "2026-11-14T09:30:00" }))["input.timeZone"]);
        Assert.Empty((await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);

        // TaskService.Run refuses the same payload before any task starts (the client goes through it too).
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await server.Host.Tasks.RunAsync(new Proto.RunTaskRequest
        {
            PluginId = DateTimeTaskPlugin.PluginId,
            DeviceIds = { device.Id.ToString() },
            PayloadJson = """{"timeZone":"Europe/Vienna","mode":"Ntp","ntp":{"source":"Static","servers":[]}}""",
        }));
        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal("Enter at least one NTP server.", refused.Status.Detail);
        Assert.Equal("ntp.servers", refused.Trailers.GetValue(TaskGrpcService.FieldTrailer));
        Assert.Empty((await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);

        // A valid payload is planned from the cached device data: a device without the Time API is skipped with the reason.
        var valid = await server.ToolAsync(key, "oadm_task_preview", new JsonObject
        {
            ["task"] = DateTimeTaskPlugin.PluginId,
            ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) },
            ["input"] = new JsonObject { ["timeZone"] = "Europe/Vienna", ["mode"] = "Ntp", ["ntp"] = new JsonObject { ["source"] = "Dhcp", ["servers"] = new JsonArray() } },
        });
        Assert.Equal(0, valid["will_run"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(valid["skipped"]!.AsArray())!["reason"]!.GetValue<string>()));
        Assert.Contains(TaskRunTools.NothingToRun, valid["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
        Assert.Null(valid["handle"]);
    }

    [Fact]
    public async Task AReadOnlyKeyListsNoWriteToolAndCanNeitherRunNorUpload()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await StartAsync(clock);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.15.0.1", 4601);
        var key = await server.CreateKeyAsync(readOnly: true);
        var writer = await server.CreateKeyAsync(name: "writer");
        var writes = server.Host.Get<ToolRegistry>().All.Where(t => !t.ReadsOnly).Select(t => t.Name).ToList();
        Assert.Contains("oadm_task_run", writes);
        Assert.Contains("oadm_files_upload_link", writes);

        var names = await server.ToolNamesAsync(key);
        Assert.Contains("oadm_task_preview", names); // a preview writes nothing
        Assert.All(writes, w => Assert.DoesNotContain(w, names));
        foreach (var tool in writes)
        {
            clock.Advance(TimeSpan.FromMinutes(2)); // each refused change is still counted: a minute apart
            var refused = await server.CallToolAsync(key, tool, tool == "oadm_task_run" ? new JsonObject { ["handle"] = "x" } : new JsonObject { ["name"] = "a.bin", ["size"] = 1 });
            Assert.True(refused["isError"]!.GetValue<bool>());
            Assert.Contains(McpAccess.ReadOnlyKey, refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        // Even a preview's handle and an upload link do not help: the run is not listed, and the upload is refused.
        var preview = await server.ToolAsync(key, "oadm_task_preview", new JsonObject { ["task"] = RestartTaskPlugin.PluginId, ["filter"] = new JsonObject(), ["input"] = new JsonObject() });
        var run = await server.CallToolAsync(key, "oadm_task_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.True(run["isError"]!.GetValue<bool>());
        Assert.Empty((await server.Host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks);
        var link = await server.ToolAsync(writer, "oadm_files_upload_link", new JsonObject { ["name"] = "a.bin", ["size"] = 3 });
        using var put = await PutAsync(server, key, link["url"]!.GetValue<string>(), [1, 2, 3]);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, put.StatusCode);
    }

    [Fact]
    public async Task AnUploadLinkTakesOneFileOfItsSizeOnceAndTheUploadIsAudited()
    {
        await using var server = await StartAsync();
        var key = await server.CreateKeyAsync();
        byte[] file = [.. Enumerable.Range(0, 300 * 1024).Select(i => (byte)i)]; // more than one chunk
        var link = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = "firmware.bin", ["size"] = file.Length });
        Assert.Contains("curl --cacert oadm-ca.pem", link["command"]!.GetValue<string>(), StringComparison.Ordinal);
        var url = link["url"]!.GetValue<string>();

        var other = await server.CreateKeyAsync(name: "other");
        using (var foreign = await PutAsync(server, other, url, file))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, foreign.StatusCode); // another key's link answers like an unknown one
        }

        using (var sent = await PutAsync(server, key, url, file))
        {
            Assert.Equal(System.Net.HttpStatusCode.OK, sent.StatusCode);
            var answer = JsonNode.Parse(await sent.Content.ReadAsStringAsync())!;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(file)), answer["sha256"]!.GetValue<string>(), ignoreCase: true);
            Assert.Equal(file.Length, answer["size"]!.GetValue<long>());
            Assert.False(string.IsNullOrEmpty(answer["file_id"]!.GetValue<string>()));
        }

        using (var twice = await PutAsync(server, key, url, file))
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, twice.StatusCode);
        }

        var small = await server.ToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = "a.bin", ["size"] = 10 });
        using (var wrong = await PutAsync(server, key, small["url"]!.GetValue<string>(), [1, 2, 3]))
        {
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        var folder = await server.CallToolAsync(key, "oadm_files_upload_link", new JsonObject { ["name"] = "../a.bin", ["size"] = 3 });
        Assert.True(folder["isError"]!.GetValue<bool>());
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.FileUploaded && e.Target == "firmware.bin" && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "agent"));
    }

    [Fact]
    public async Task EachKeyChangesAtMostTenTimesAMinuteThen429()
    {
        await using var server = await StartAsync();
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.16.0.1", 4701);
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(await server.Host.InvokerForUserAsync("second", Oadm.Sdk.Plugins.UserRole.Admin), name: "other"); // another user's budget
        var access = server.Host.Get<McpAccess>();
        var caller = (await access.AuthenticateAsync(key, "local", CancellationToken.None))!;
        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.True(access.TryChange(caller)); // the key's runs of this minute (counted as the endpoint does)
        }

        JsonObject Preview() => new() { ["task"] = RestartTaskPlugin.PluginId, ["filter"] = new JsonObject { ["ids"] = new JsonArray(device.Id.ToString()) }, ["input"] = new JsonObject() };
        var preview = await server.ToolAsync(key, "oadm_task_preview", Preview()); // previews are not runs
        var refused = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_task_run", ["arguments"] = new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() } }));
        Assert.Equal(429, refused.Status);
        Assert.Equal("60", refused.RetryAfter);
        Assert.Equal(McpEndpoint.TooManyChanges, refused.ErrorMessage);

        var otherPreview = await server.ToolAsync(other, "oadm_task_preview", Preview());
        Assert.Equal(1, (await server.ToolAsync(other, "oadm_task_run", new JsonObject { ["handle"] = otherPreview["handle"]!.GetValue<string>() }))["tasks"]!.GetValue<int>());
    }

    [Fact]
    public void HandlesEndAfterTenMinutesAndBelongToTheirCaller()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handles = new ToolHandles(time);
        var run = new PlannedRun("oadm.restart", [Guid.NewGuid().ToString()], null, "Restart device");
        var first = handles.AddRun("key-a", run);
        Assert.Null(handles.TakeRun("key-b", first)); // another caller's: unknown, and it stays theirs
        Assert.Same(run, handles.TakeRun("key-a", first));
        Assert.Null(handles.TakeRun("key-a", first)); // once

        var late = handles.AddRun("key-a", run);
        time.Advance(ToolHandles.Lifetime);
        Assert.Null(handles.TakeRun("key-a", late));
    }

    private static async Task<HttpResponseMessage> PutAsync(McpTestServer server, string key, string url, byte[] body)
    {
        using var client = server.Http();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await client.SendAsync(request);
    }
}
