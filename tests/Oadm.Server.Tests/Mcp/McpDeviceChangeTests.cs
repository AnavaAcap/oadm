using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The device tools of the devices part (docs/spec/mcp.md "Devices and the device filter"): the snapshot as image content, and
/// Refresh, Remove, Set credentials (by the name of a stored credential) and Add devices as a preview that writes nothing and a
/// run that takes its handle; the read only settings. Handles run once, belong to their key and to their own run tool, a Read
/// only key and a role without the permission list no run, a device outside the group answers like an unknown one, and no
/// password is in any answer.
/// </summary>
public sealed class McpDeviceChangeTests
{
    private const string StoredPassword = "Install-pw-1";
    private const string Password = "s3cret-Pass";

    private static readonly string[] RunTools =
    [
        "oadm_devices_refresh_run", "oadm_devices_remove_run", "oadm_devices_credentials_run", "oadm_devices_add_run",
    ];

    private static FakeAxisNetwork Network()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), Password);
        network.Add("10.9.0.2", FakeSerials.Make(2), "other-password");
        network.Add("10.9.0.3", FakeSerials.Make(3), password: null); // factory default
        return network;
    }

    private static async Task<McpTestServer> StartAsync(FakeAxisNetwork? network = null, FakeCamera? camera = null)
    {
        var server = await McpTestServer.StartAsync(network, configureServices: services =>
        {
            if (camera is not null)
            {
                services.RemoveAll<ILiveVideoSourceFactory>();
                services.AddSingleton<ILiveVideoSourceFactory>(camera);
            }
        });
        return server;
    }

    /// <summary>A camera that answers pictures and records what was asked.</summary>
    private sealed class FakeCamera : ILiveVideoSourceFactory
    {
        private readonly Lock _gate = new();
        private readonly List<(Guid Device, int Camera, ImageSize Size, int Compression)> _snapshots = [];

        /// <summary>The picture for a requested size.</summary>
        public Func<ImageSize, byte[]> Picture { get; init; } = _ => Jpeg(2_000);

        public IReadOnlyList<(Guid Device, int Camera, ImageSize Size, int Compression)> Snapshots
        {
            get
            {
                lock (_gate)
                {
                    return [.. _snapshots];
                }
            }
        }

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(new LiveViewCapabilities([VideoCodecKind.H264], [new(1920, 1080), new(1280, 720), new(640, 360)]));

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct) => throw new NotSupportedException();

        public Task<byte[]> GetSnapshotAsync(Guid deviceId, int camera, ImageSize size, int compression, CancellationToken ct)
        {
            lock (_gate)
            {
                _snapshots.Add((deviceId, camera, size, compression));
            }

            return Task.FromResult(Picture(size));
        }
    }

    private static byte[] Jpeg(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i * 31);
        }

        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        return bytes;
    }

    private static string TextOf(JsonObject result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static async Task<IReadOnlyDictionary<string, string>> ListAsync(TestServerHost host) =>
        (await host.Devices.ListAsync(new Proto.Empty())).Devices.ToDictionary(d => d.Address, d => d.Id);

    // ---- snapshot ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASnapshotIsOneJpegAsImageContentAtMost1280WideAndTheTextHasItsSize()
    {
        var camera = new FakeCamera();
        await using var server = await StartAsync(camera: camera);
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.30.0.1", 5001);
        var key = await server.CreateKeyAsync();

        var result = await server.CallToolAsync(key, "oadm_devices_snapshot", new JsonObject { ["device_id"] = device.Id.ToString() });
        Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
        var content = result["content"]!.AsArray();
        Assert.Equal(2, content.Count);
        var picture = content[1]!.AsObject();
        Assert.Equal("image", picture["type"]!.GetValue<string>());
        Assert.Equal("image/jpeg", picture["mimeType"]!.GetValue<string>());
        var bytes = Convert.FromBase64String(picture["data"]!.GetValue<string>());
        Assert.Equal(Jpeg(2_000), bytes);
        Assert.Equal(1280, result["structuredContent"]!["width"]!.GetValue<int>());
        Assert.Equal(720, result["structuredContent"]!["height"]!.GetValue<int>());
        Assert.Equal(2_000, result["structuredContent"]!["bytes"]!.GetValue<int>());
        var asked = Assert.Single(camera.Snapshots);
        Assert.Equal((device.Id, 1, new ImageSize(1280, 720)), (asked.Device, asked.Camera, asked.Size));
    }

    [Fact]
    public async Task AnAnswerThatIsTooLargeIsAskedAgainSmallerAndNeverSentOverTheLimit()
    {
        var camera = new FakeCamera { Picture = size => Jpeg(size.Width >= 1280 ? ToolAnswers.MaxImageBytes + 1 : 90_000) };
        await using var server = await StartAsync(camera: camera);
        var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.30.0.2", 5002);
        var key = await server.CreateKeyAsync();

        var result = await server.CallToolAsync(key, "oadm_devices_snapshot", new JsonObject { ["device_id"] = device.Id.ToString() });
        Assert.Equal(640, result["structuredContent"]!["width"]!.GetValue<int>());
        Assert.Equal(3, camera.Snapshots.Count); // 1280 sharp, 1280 smaller, then 640
        Assert.All(result["content"]!.AsArray().Skip(1), c => Assert.True(Convert.FromBase64String(c!["data"]!.GetValue<string>()).Length <= ToolAnswers.MaxImageBytes));

        var huge = new FakeCamera { Picture = _ => Jpeg(ToolAnswers.MaxImageBytes + 1) };
        await using var other = await StartAsync(camera: huge);
        var second = await DeviceServiceTests.AddDeviceAsync(other.Host, "10.30.0.3", 5003);
        var refused = await other.CallToolAsync(await other.CreateKeyAsync(), "oadm_devices_snapshot", new JsonObject { ["device_id"] = second.Id.ToString() });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains(DeviceChangeTools.RefusedPicture, TextOf(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASnapshotNeedsLiveViewAndADeviceOutsideTheGroupAnswersLikeAnUnknownOne()
    {
        var camera = new FakeCamera();
        await using var server = await StartAsync(camera: camera);
        var inside = await GroupCallers.DeviceAsync(server.Host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(server.Host, 2);

        // A role without Live view does not list it.
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Agents", Permissions = { HostPermissions.McpUse } } } });
        var role = Guid.Parse(saved.Roles.Single(r => r.Name == "Agents").Id);
        var agent = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("agent1", role));
        Assert.DoesNotContain("oadm_devices_snapshot", await server.ToolNamesAsync(agent));
        var denied = await server.CallToolAsync(agent, "oadm_devices_snapshot", new JsonObject { ["device_id"] = inside.Id.ToString() });
        Assert.True(denied["isError"]!.GetValue<bool>());
        Assert.Contains("Your role does not allow:", TextOf(denied), StringComparison.Ordinal);
        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.ToString(), Name = "Agents", Permissions = { HostPermissions.McpUse, HostPermissions.DevicesLiveView } } } });
        Assert.Contains("oadm_devices_snapshot", await server.ToolNamesAsync(agent));
        Assert.Empty(camera.Snapshots);

        // A user of a device group sees its own devices; an outside device answers exactly like an unknown id and the camera is never asked.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var key = await server.CreateKeyAsync(group.Invoker);
        var ok = await server.CallToolAsync(key, "oadm_devices_snapshot", new JsonObject { ["device_id"] = inside.Id.ToString() });
        Assert.False(ok["isError"]?.GetValue<bool>() ?? false, ok.ToJsonString());
        var hidden = await server.CallToolAsync(key, "oadm_devices_snapshot", new JsonObject { ["device_id"] = outside.Id.ToString() });
        var unknown = await server.CallToolAsync(key, "oadm_devices_snapshot", new JsonObject { ["device_id"] = Guid.NewGuid().ToString() });
        Assert.True(hidden["isError"]!.GetValue<bool>());
        Assert.Equal(TextOf(unknown), TextOf(hidden));
        Assert.Equal([inside.Id], camera.Snapshots.Select(s => s.Device));
    }

    // ---- refresh, remove, credentials ----------------------------------------------------------------------------------------

    [Fact]
    public async Task RefreshQueuesTheFilterDevicesOfThePreviewAndItsHandleRunsOnceForItsKeyAndItsRunTool()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.31.0.1", FakeSerials.Make(5101), "pw");
        network.Add("10.31.0.2", FakeSerials.Make(5102), "pw");
        await using var server = await StartAsync(network);
        var first = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.31.0.1", 5101, status: DeviceStatus.Unknown);
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.31.0.2", 5102, status: DeviceStatus.Unknown);
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "other");

        var preview = await server.ToolAsync(key, "oadm_devices_refresh_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(first.Id.ToString()) } });
        Assert.Equal(1, preview["devices"]!.GetValue<int>());
        Assert.Equal(first.Id.ToString(), Assert.Single(preview["first"]!.AsArray())!["id"]!.GetValue<string>());
        var handle = preview["handle"]!.GetValue<string>();

        // Another key and another run tool cannot take it; it stays for its own.
        Assert.True((await server.CallToolAsync(other, "oadm_devices_refresh_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_devices_remove_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.Equal(2, (await ListAsync(server.Host)).Count);

        var run = await server.ToolAsync(key, "oadm_devices_refresh_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(1, run["devices"]!.GetValue<int>());
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.GetDeviceAsync(server.Host, first.Id.ToString())) is { Status: Proto.DeviceStatus.Ok, HasDhcpEnabled: true },
            "the refresh of the previewed device");
        var again = await server.CallToolAsync(key, "oadm_devices_refresh_run", new JsonObject { ["handle"] = handle });
        Assert.True(again["isError"]!.GetValue<bool>());
        Assert.Contains("oadm_devices_refresh_preview", TextOf(again), StringComparison.Ordinal);

        var none = await server.ToolAsync(key, "oadm_devices_refresh_preview", new JsonObject { ["filter"] = new JsonObject { ["search"] = "no such device" } });
        Assert.Equal(0, none["devices"]!.GetValue<int>());
        Assert.Null(none["handle"]);
        Assert.Contains(DeviceChangeTools.NoDevices, none["warnings"]!.AsArray().Select(w => w!.GetValue<string>()));
    }

    [Fact]
    public async Task RemoveNeedsAFilterThePreviewRemovesNothingAndTheRunRemovesExactlyThoseDevicesAndIsAudited()
    {
        await using var server = await StartAsync();
        for (var i = 1; i <= 3; i++)
        {
            var device = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.32.0." + i, 5200 + i);
            if (i < 3)
            {
                await server.Host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { "Old" } });
            }
        }

        var key = await server.CreateKeyAsync();
        var empty = await server.CallToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject() });
        Assert.True(empty["isError"]!.GetValue<bool>());
        Assert.Equal("filter", JsonNode.Parse(TextOf(empty))!["errors"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(3, (await ListAsync(server.Host)).Count);

        var preview = await server.ToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject { ["tags"] = new JsonArray("Old") } });
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        Assert.Equal(["10.32.0.1", "10.32.0.2"], preview["first"]!.AsArray().Select(r => r!["address"]!.GetValue<string>()));
        Assert.Equal(3, (await ListAsync(server.Host)).Count); // a preview writes nothing

        var run = await server.ToolAsync(key, "oadm_devices_remove_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(2, run["devices"]!.GetValue<int>());
        Assert.Equal(["10.32.0.3"], (await ListAsync(server.Host)).Keys);
        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.DevicesRemoved && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "agent"));
        Assert.True((await server.CallToolAsync(key, "oadm_devices_remove_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() }))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ADeviceGroupRemovesOnlyItsOwnDevicesAndNeverListsTheOthers()
    {
        await using var server = await StartAsync();
        var inside = await GroupCallers.DeviceAsync(server.Host, 1, "Lobby");
        var outside = await GroupCallers.DeviceAsync(server.Host, 2);
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var key = await server.CreateKeyAsync(group.Invoker);

        var preview = await server.ToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject { ["search"] = "10.9.1." } });
        Assert.Equal(1, preview["devices"]!.GetValue<int>());
        Assert.DoesNotContain(outside.Id.ToString(), preview.ToJsonString(), StringComparison.Ordinal);

        // Naming an outside device by id answers like an unknown id.
        var named = await server.CallToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(outside.Id.ToString()) } });
        var unknown = await server.CallToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(Guid.NewGuid().ToString()) } });
        Assert.True(named["isError"]!.GetValue<bool>());
        Assert.Equal(TextOf(unknown), TextOf(named));

        await server.ToolAsync(key, "oadm_devices_remove_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal([outside.Address], (await ListAsync(server.Host)).Keys);
        Assert.NotEqual(inside.Id, outside.Id);

        // The tools that use the credential list stay with the Administrator; adding needs a device group with a site.
        var names = await server.ToolNamesAsync(key);
        Assert.DoesNotContain("oadm_devices_credentials_preview", names);
        var add = await server.CallToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["addresses"] = new JsonArray("10.9.1.50") });
        Assert.True(add["isError"]!.GetValue<bool>());
        Assert.Contains(Oadm.Sdk.Devices.DeviceGroupMessages.CannotAdd, TextOf(add), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACredentialIsChosenByItsNameNoPasswordIsEverSentOrShownAndTheRunStoresIt()
    {
        await using var server = await StartAsync();
        var one = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.33.0.1", 5301, password: "old-pw");
        var two = await DeviceServiceTests.AddDeviceAsync(server.Host, "10.33.0.2", 5302, password: "old-pw");
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "installer", Password = StoredPassword });
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "second", Password = "Second-pw-2" });
        var key = await server.CreateKeyAsync();
        var answers = new List<string>();
        JsonObject Filter() => new() { ["search"] = "10.33.0." };

        // Without a name the preview lists the names and offers no handle.
        var names = await server.CallToolAsync(key, "oadm_devices_credentials_preview", new JsonObject { ["filter"] = Filter() });
        answers.Add(names.ToJsonString());
        var listed = names["structuredContent"]!;
        Assert.Null(listed["handle"]);
        Assert.Equal(["installer", "second"], listed["stored_credentials"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(2, listed["devices"]!.GetValue<int>());

        var unknown = await server.CallToolAsync(key, "oadm_devices_credentials_preview", new JsonObject { ["filter"] = Filter(), ["credential"] = "nobody" });
        Assert.True(unknown["isError"]!.GetValue<bool>());
        Assert.Equal("credential", JsonNode.Parse(TextOf(unknown))!["errors"]![0]!["field"]!.GetValue<string>());

        var preview = await server.ToolAsync(key, "oadm_devices_credentials_preview", new JsonObject { ["filter"] = Filter(), ["credential"] = "installer" });
        answers.Add(preview.ToJsonString());
        Assert.Equal("installer", preview["credential"]!.GetValue<string>());
        Assert.Equal(2, preview["devices"]!.GetValue<int>());
        var store = server.Host.Get<CredentialStore>();
        Assert.Equal("root", (await store.GetAsync(one.Id, CancellationToken.None))!.UserName); // a preview writes nothing

        var run = await server.ToolAsync(key, "oadm_devices_credentials_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        answers.Add(run.ToJsonString());
        Assert.Equal(2, run["devices"]!.GetValue<int>());
        foreach (var device in new[] { one, two })
        {
            var stored = await store.GetAsync(device.Id, CancellationToken.None);
            Assert.Equal(("installer", StoredPassword), (stored!.UserName, stored.Password));
        }

        var audit = (await server.Host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.DeviceCredentialsSet && e.UserName == AuditActions.ViaAccessKey(TestServerHost.AdminUserName, "agent"));
        Assert.All(answers, a => Assert.DoesNotContain(StoredPassword, a, StringComparison.Ordinal));
        Assert.All(answers, a => Assert.DoesNotContain("Second-pw-2", a, StringComparison.Ordinal));

        // Two entries with one name cannot be chosen by name.
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "installer", Password = "Third-pw-3" });
        var ambiguous = await server.CallToolAsync(key, "oadm_devices_credentials_preview", new JsonObject { ["filter"] = Filter(), ["credential"] = "installer" });
        Assert.True(ambiguous["isError"]!.GetValue<bool>());
        Assert.Contains(DeviceChangeTools.AmbiguousCredential, TextOf(ambiguous), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleListsAChangeToolOnlyWhenItMayRunItAndAReadOnlyKeyListsNoRun()
    {
        await using var server = await StartAsync();
        await DeviceServiceTests.AddDeviceAsync(server.Host, "10.34.0.1", 5401);
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Agents", Permissions = { HostPermissions.McpUse, HostPermissions.DevicesRefresh } } } });
        var role = Guid.Parse(saved.Roles.Single(r => r.Name == "Agents").Id);
        var agent = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("agent1", role));
        var names = await server.ToolNamesAsync(agent);
        Assert.Contains("oadm_devices_refresh_preview", names);
        Assert.Contains("oadm_devices_refresh_run", names);
        Assert.DoesNotContain("oadm_devices_remove_preview", names); // "Add and remove devices"
        Assert.DoesNotContain("oadm_devices_remove_run", names);
        Assert.DoesNotContain("oadm_devices_add_preview", names);
        Assert.DoesNotContain("oadm_devices_add_run", names);
        Assert.DoesNotContain("oadm_devices_credentials_run", names);
        Assert.DoesNotContain("oadm_devices_snapshot", names);

        var readOnly = await server.CreateKeyAsync(name: "reader", readOnly: true);
        var readNames = await server.ToolNamesAsync(readOnly);
        foreach (var preview in new[] { "oadm_devices_refresh_preview", "oadm_devices_remove_preview", "oadm_devices_credentials_preview", "oadm_devices_add_preview", "oadm_devices_snapshot", "oadm_settings_get" })
        {
            Assert.Contains(preview, readNames);
        }

        Assert.All(RunTools, run => Assert.DoesNotContain(run, readNames));
        var tools = server.Host.Get<ToolRegistry>().All.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Assert.All(RunTools, run => Assert.False(tools[run].ReadsOnly));
        Assert.True(tools["oadm_devices_remove_run"].Destructive);
        Assert.False(tools["oadm_devices_add_run"].Destructive); // it only adds
        foreach (var run in RunTools)
        {
            var refused = await server.CallToolAsync(readOnly, run, new JsonObject { ["handle"] = "x" });
            Assert.True(refused["isError"]!.GetValue<bool>());
            Assert.Contains(McpAccess.ReadOnlyKey, TextOf(refused), StringComparison.Ordinal);
        }
    }

    // ---- add devices ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAddPreviewScansAndLogsInWithTheCredentialListWritesNothingAndTheRunAddsOnlyTheLoggedInDevices()
    {
        await using var server = await StartAsync(Network());
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        var key = await server.CreateKeyAsync();
        var other = await server.CreateKeyAsync(name: "other");

        var preview = await server.ToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["range"] = new JsonObject { ["from"] = "10.9.0.1", ["to"] = "10.9.0.6" } });
        Assert.Equal("range", preview["source"]!.GetValue<string>());
        Assert.True(preview["complete"]!.GetValue<bool>());
        Assert.Equal(3, preview["found"]!.GetValue<int>());
        Assert.Equal(1, preview["will_add"]!.GetValue<int>());
        var states = preview["states"]!.AsArray().ToDictionary(s => s!["state"]!.GetValue<string>(), s => s!["count"]!.GetValue<int>());
        Assert.Equal(new Dictionary<string, int> { ["Authenticated"] = 1, ["LoginFailed"] = 1, ["PasswordNotSet"] = 1 }, states);
        var first = preview["first"]!.AsArray();
        Assert.Equal(("10.9.0.1", "Authenticated"), (first[0]!["address"]!.GetValue<string>(), first[0]!["state"]!.GetValue<string>()));
        Assert.DoesNotContain(Password, preview.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("other-password", preview.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains(preview["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("factory default", StringComparison.Ordinal));

        // The scan contacted devices and wrote nothing to OADM.
        Assert.Empty(await ListAsync(server.Host));
        Assert.Equal(0, (await new Proto.WaitingDeviceService.WaitingDeviceServiceClient(server.Host.Invoker).ListAsync(new Proto.ListWaitingRequest())).TotalCount);

        var handle = preview["handle"]!.GetValue<string>();
        Assert.True((await server.CallToolAsync(other, "oadm_devices_add_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        Assert.True((await server.CallToolAsync(key, "oadm_devices_refresh_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
        var run = await server.ToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = handle });
        Assert.Equal(1, run["added"]!.GetValue<int>());
        Assert.Equal(0, run["not_added"]!.GetValue<int>());

        var added = await ListAsync(server.Host);
        Assert.Equal(["10.9.0.1"], added.Keys); // the device that logged in; not the wrong password, not the factory default
        var stored = await server.Host.Get<CredentialStore>().GetAsync(Guid.Parse(added["10.9.0.1"]), CancellationToken.None);
        Assert.Equal(("root", Password), (stored!.UserName, stored.Password));
        Assert.DoesNotContain(Password, run.ToJsonString(), StringComparison.Ordinal);
        Assert.True((await server.CallToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = handle }))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AddressesAreProbedOneByOneAndOnesThatDoNotAnswerStayOnTheWaitingListOnlyWhenAsked()
    {
        await using var server = await StartAsync(Network());
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        var key = await server.CreateKeyAsync();
        var waiting = new Proto.WaitingDeviceService.WaitingDeviceServiceClient(server.Host.Invoker);
        var input = new JsonObject { ["addresses"] = new JsonArray("10.9.0.1", "10.9.0.77", "10.9.0.2"), ["keep_waiting"] = true };

        var preview = await server.ToolAsync(key, "oadm_devices_add_preview", input);
        Assert.Equal("addresses", preview["source"]!.GetValue<string>());
        Assert.Equal(2, preview["found"]!.GetValue<int>());
        Assert.Equal(1, preview["will_add"]!.GetValue<int>());
        Assert.Equal(["10.9.0.77"], preview["not_answering"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(1, preview["keep_waiting"]!.GetValue<int>());
        Assert.Equal(0, (await waiting.ListAsync(new Proto.ListWaitingRequest())).TotalCount);

        var run = await server.ToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = preview["handle"]!.GetValue<string>() });
        Assert.Equal(1, run["added"]!.GetValue<int>());
        Assert.Equal(1, run["waiting_kept"]!.GetValue<int>());
        var kept = Assert.Single((await waiting.ListAsync(new Proto.ListWaitingRequest())).Devices);
        Assert.Equal("10.9.0.77", kept.Address);
        Assert.False(kept.HasLogin); // no login is kept with it
        Assert.Equal(["10.9.0.1"], (await ListAsync(server.Host)).Keys);

        // Without keep_waiting nothing is kept; with nothing to add there is no handle.
        var plain = await server.ToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["addresses"] = new JsonArray("10.9.0.88") });
        Assert.Null(plain["handle"]);
        Assert.Equal(0, plain["keep_waiting"]!.GetValue<int>());
        Assert.Equal(1, plain["not_answering_count"]!.GetValue<int>());

        // The waiting list can be tried now: a preview of it, then the run.
        var tryNow = await server.ToolAsync(key, "oadm_devices_add_preview", new JsonObject { ["try_waiting"] = true });
        Assert.Equal("waiting", tryNow["source"]!.GetValue<string>());
        Assert.Equal(1, tryNow["will_add"]!.GetValue<int>());
        var tried = await server.ToolAsync(key, "oadm_devices_add_run", new JsonObject { ["handle"] = tryNow["handle"]!.GetValue<string>() });
        Assert.Equal(1, tried["waiting_tried"]!.GetValue<int>());
        Assert.Equal(0, tried["added"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheAddInputIsCheckedOnTheServerWithTheClientsLimitsOnTheRangeSize()
    {
        await using var server = await StartAsync(Network());
        var key = await server.CreateKeyAsync();

        async Task<Dictionary<string, string>> ErrorsAsync(JsonObject input)
        {
            var result = await server.CallToolAsync(key, "oadm_devices_add_preview", input);
            Assert.True(result["isError"]!.GetValue<bool>(), result.ToJsonString());
            return JsonNode.Parse(TextOf(result))!["errors"]!.AsArray().ToDictionary(e => e!["field"]!.GetValue<string>(), e => e!["error"]!.GetValue<string>());
        }

        Assert.Equal($"The range may contain at most {Oadm.Core.Discovery.Ipv4Range.MaxSize} addresses.", (await ErrorsAsync(new JsonObject { ["range"] = new JsonObject { ["from"] = "10.0.0.0", ["to"] = "10.2.0.0" } }))["range.to"]);
        Assert.Equal("The last address must not be lower than the first.", (await ErrorsAsync(new JsonObject { ["range"] = new JsonObject { ["from"] = "10.0.0.9", ["to"] = "10.0.0.1" } }))["range.to"]);
        Assert.True((await ErrorsAsync(new JsonObject())).ContainsKey("range"));
        Assert.True((await ErrorsAsync(new JsonObject { ["range"] = new JsonObject { ["from"] = "10.0.0.1", ["to"] = "10.0.0.2" }, ["addresses"] = new JsonArray("10.0.0.3") })).ContainsKey("range"));
        Assert.True((await ErrorsAsync(new JsonObject { ["range"] = new JsonObject { ["from"] = "10.0.0.1", ["to"] = "10.0.0.2" }, ["keep_waiting"] = true })).ContainsKey("keep_waiting"));
        Assert.True((await ErrorsAsync(new JsonObject { ["addresses"] = new JsonArray("10.0.0.3", "ftp://cam") })).ContainsKey("addresses[1]"));
        Assert.Empty(await ListAsync(server.Host));
    }

    // ---- settings ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSettingsAreReadOnlyAndHoldNoSecret()
    {
        await using var server = await StartAsync();
        await server.Host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "installer", Password = StoredPassword });
        var key = await server.CreateKeyAsync(readOnly: true);
        var settings = await server.Host.Settings.GetAsync(new Proto.Empty());

        var answer = await server.ToolAsync(key, "oadm_settings_get");
        Assert.Equal(settings.ServerName, answer["server_name"]!.GetValue<string>());
        Assert.Equal(settings.ListenUrl, answer["listen_url"]!.GetValue<string>());
        Assert.Equal(settings.ScanParallelism, answer["scan"]!["parallelism"]!.GetValue<int>());
        Assert.Equal(settings.ScanTimeoutMs, answer["scan"]!["timeout_ms"]!.GetValue<int>());
        Assert.Equal(settings.ZeroConfSeconds, answer["zero_conf_seconds"]!.GetValue<int>());
        Assert.Equal(settings.WaitingRetryMinutes, answer["waiting_retry_minutes"]!.GetValue<int>());
        Assert.Equal(settings.MaxParallelTasksPerPlugin, answer["max_parallel_tasks_per_plugin"]!.GetValue<int>());
        Assert.Equal(settings.AllowBasicOverHttp, answer["allow_basic_over_http"]!.GetValue<bool>());
        Assert.Equal(settings.ClockToleranceSeconds, answer["clock_tolerance_seconds"]!.GetValue<int>());
        Assert.Equal(settings.VulnerabilityUpdate, answer["vulnerability_update"]!.GetValue<string>());

        // Nothing of the server's id, license, notices or credential list; and the tool changes nothing.
        var text = answer.ToJsonString();
        Assert.DoesNotContain(settings.ServerId, text, StringComparison.Ordinal);
        Assert.DoesNotContain("installer", text, StringComparison.Ordinal);
        Assert.DoesNotContain(StoredPassword, text, StringComparison.Ordinal);
        Assert.True(server.Host.Get<ToolRegistry>().All.Single(t => t.Name == "oadm_settings_get").ReadsOnly);
        Assert.DoesNotContain("/oadm.v1.SettingsService/Set", server.Host.Get<ToolRegistry>().All.SelectMany(t => t.Methods));
    }
}
