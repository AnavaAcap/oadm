using System.Text;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Contracts.Assistant;
using Oadm.Core.Auth;
using Oadm.Core.Persistence;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Mcp;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Assistant;

/// <summary>
/// Files and secrets in the assistant (docs/spec/assistant.md rule 3, docs/spec/mcp.md "Files", "Secrets"): a file a tool
/// offered is a card the user saves through <c>AssistantService.Download</c> (own chat only), and a secret of a preview is
/// typed into the card and goes with Run straight to the server: never to the model, into the chat or a log line.
/// </summary>
public sealed class AssistantTransferTests
{
    private const string Canary = "canary-assistant-secret-91fe";

    [Fact]
    public async Task TheCardTakesTheSecretWithRunAndTheModelTheChatAndTheLogsNeverSeeIt()
    {
        SecretDemoTools? demo = null;
        await using var server = await AssistantTestServer.StartAsync(configureServices: services =>
            services.AddSingleton<IToolSource>(sp => demo = new SecretDemoTools(sp.GetRequiredService<ToolHandles>())));
        server.Model.Call((SecretDemoTools.Preview, AssistantTestServer.Args("""{"name":"Alarm desk"}"""))).Say("Enter the password and press Run.");

        var events = await server.ChatAsync("Save the recipient Alarm desk");

        var card = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        var field = Assert.Single(card.Secrets);
        Assert.Equal("password", field.Name);
        Assert.Equal("password", field.Label);
        Assert.DoesNotContain(card.Details, d => d.Contains("secret", StringComparison.OrdinalIgnoreCase));
        var toModel = server.Model.Requests[1].Results().Values.Single();
        Assert.Equal(AssistantChats.SecretNote, toModel["note"]!.GetValue<string>());
        Assert.Null(toModel["secret_links"]);

        // Without the field Run is refused and the card stays; with it the run gets the value once.
        var missing = await Assert.ThrowsAsync<RpcException>(async () =>
            await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle }));
        Assert.Equal(StatusCode.FailedPrecondition, missing.StatusCode);
        Assert.Equal(AssistantTexts.EnterSecret("password"), missing.Status.Detail);
        Assert.Equal("password", missing.Trailers.GetValue(AssistantTexts.FieldTrailer));
        var unknown = await Assert.ThrowsAsync<RpcException>(async () =>
            await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle, Secrets = { ["pin"] = "1" } }));
        Assert.Equal(StatusCode.InvalidArgument, unknown.StatusCode);

        var run = new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle };
        run.Secrets["password"] = Canary;
        await server.Service().RunAsync(run);
        Assert.Equal(Canary, Assert.Single(demo!.Received));

        // The next message carries the run's answer to the model: never the value.
        server.Model.Say("Saved.");
        var next = await server.ChatAsync("Thanks");
        foreach (var request in server.Model.Requests)
        {
            Assert.DoesNotContain(Canary, request.AllText(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Canary, string.Concat(events.Concat(next).Select(e => e.ToString())), StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory), StringComparison.Ordinal);
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.DoesNotContain(audit, e => string.Join('|', e.Target, e.Detail).Contains(Canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFileOfAToolAnswerIsACardSavedOnlyFromItsOwnChat()
    {
        await using var server = await AssistantTestServer.StartAsync();
        await GroupCallers.DeviceAsync(server.Host, 71);
        server.Model.Call(("oadm_devices_export", AssistantTestServer.Args("""{"filter":{}}"""))).Say("Save the file.");

        var events = await server.ChatAsync("Export the devices");

        var file = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.File).File;
        Assert.EndsWith(".csv", file.Name, StringComparison.Ordinal);
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal(AssistantChats.FileNote, server.Model.Requests[1].Results().Values.Single()["note"]!.GetValue<string>());

        var bytes = await DownloadAsync(server.Service(), "chat-1", file.FileRef);
        Assert.Contains("MAC address,Status,Address", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains(await DownloadAsync(server.Service(), "chat-1", file.FileRef), b => b != 0); // saved again from the same card

        var other = await server.Host.InvokerForUserAsync("other-admin", Oadm.Sdk.Plugins.UserRole.Admin);
        foreach (var (client, chat) in new[] { (server.Service(other), "chat-1"), (server.Service(), "chat-2") })
        {
            var refused = await Assert.ThrowsAsync<RpcException>(() => DownloadAsync(client, chat, file.FileRef));
            Assert.Equal(StatusCode.NotFound, refused.StatusCode);
            Assert.Equal(AssistantTexts.FileEnded, refused.Status.Detail);
        }

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Equal(2, audit.Count(e => e.Action == AuditActions.FileDownloaded && e.Target == file.Name));
    }

    [Fact]
    public async Task BackUpNowTakesThePasswordTwiceInTheCardAndItsRunAnswersTheFileCard()
    {
        await using var server = await AssistantTestServer.StartAsync(configureServices: services =>
        {
            services.AddSingleton(sp => new Oadm.Core.Backup.BackupArchive(sp.GetRequiredService<OadmPaths>()) { Iterations = 1000 });
        });
        server.Model.Call(("oadm_backups_create_preview", AssistantTestServer.Args("{}"))).Say("Enter the password twice and press Run.");

        var events = await server.ChatAsync("Back up the server now");

        var card = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        Assert.Equal(["password", "password_again"], card.Secrets.Select(s => s.Name));
        Assert.Equal("1 backup", card.CountText);
        var run = new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle };
        run.Secrets["password"] = Canary;
        run.Secrets["password_again"] = Canary;
        var reply = await server.Service().RunAsync(run);

        var file = Assert.Single(reply.Files);
        Assert.EndsWith(".oadmbackup", file.Name, StringComparison.Ordinal);
        var bytes = await DownloadAsync(server.Service(), "chat-1", file.FileRef);
        Assert.Equal("OADMBACKUP", Encoding.ASCII.GetString(bytes, 0, 10));
        Assert.Contains((await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries, e => e.Action == AuditActions.BackupCreated);
        foreach (var request in server.Model.Requests)
        {
            Assert.DoesNotContain(Canary, request.AllText(), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Canary, McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory), StringComparison.Ordinal);
    }

    private static async Task<byte[]> DownloadAsync(Proto.AssistantService.AssistantServiceClient client, string chat, string fileRef)
    {
        using var call = client.Download(new Proto.AssistantDownloadRequest { ConversationId = chat, FileRef = fileRef });
        using var bytes = new MemoryStream();
        await foreach (var chunk in call.ResponseStream.ReadAllAsync())
        {
            chunk.Data.WriteTo(bytes);
        }

        return bytes.ToArray();
    }

    [Fact]
    public async Task ANewAccessKeyIsACardWhoseCopyFetchesItOnceAndTheModelNeverSeesIt()
    {
        await using var server = await AssistantTestServer.StartAsync();
        server.Model.Call(("oadm_access_keys_create_preview", AssistantTestServer.Args("""{"name":"Desk","role":"Administrator"}"""))).Say("Press Run to create it.");

        var events = await server.ChatAsync("Make an access key for the desk agent");
        var card = Assert.Single(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Preview).Preview;
        Assert.Contains(card.Details, d => d == "Creates the access key: Desk");
        Assert.Contains(card.Warnings, w => w.Contains("The new key has the role Administrator and all devices", StringComparison.Ordinal));

        var reply = await server.Service().RunAsync(new Proto.AssistantRunRequest { ConversationId = "chat-1", Handle = card.Handle });
        Assert.Equal("Access key Desk", reply.Secret.Title);
        var key = Encoding.UTF8.GetString(await DownloadAsync(server.Service(), "chat-1", reply.Secret.FileRef));
        Assert.StartsWith(AccessKeyStore.Prefix, key, StringComparison.Ordinal);
        Assert.NotNull(await server.Host.Get<AccessKeyStore>().ValidateAsync(key, CancellationToken.None));

        // Copy fetches it once; another chat or user never.
        var twice = await Assert.ThrowsAsync<RpcException>(() => DownloadAsync(server.Service(), "chat-1", reply.Secret.FileRef));
        Assert.Equal(StatusCode.NotFound, twice.StatusCode);
        Assert.Equal(OneTimeSecret.Taken, twice.Status.Detail);

        server.Model.Say("Done.");
        var next = await server.ChatAsync("Thanks");
        foreach (var part in new[] { key, key[AccessKeyStore.Prefix.Length..], reply.Secret.FileRef })
        {
            foreach (var request in server.Model.Requests)
            {
                Assert.DoesNotContain(part, request.AllText(), StringComparison.Ordinal);
            }

            Assert.DoesNotContain(part, string.Concat(events.Concat(next).Select(e => e.ToString())), StringComparison.Ordinal);
            Assert.DoesNotContain(part, McpSecretTests.ReadLogs(server.Host.Get<OadmPaths>().LogsDirectory), StringComparison.Ordinal);
        }

        Assert.Contains(AccessKeyTools.CardNote, server.Model.Requests[^1].AllText(), StringComparison.Ordinal);
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.DoesNotContain(audit, e => string.Join('|', e.Target, e.Detail).Contains(key, StringComparison.Ordinal));
        Assert.Contains(audit, e => e.Action == AuditActions.AccessKeyCreated && e.Target == "Desk");
    }
}
