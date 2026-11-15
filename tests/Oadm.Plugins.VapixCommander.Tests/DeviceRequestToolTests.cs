using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// The agents' device request (<c>oadm_device_request</c>, docs/spec/mcp.md "VAPIX Commander", decision 2026-11-15): the page's
/// request code, paths of the device only, the right "dangerous" for requests that can lock OADM out (a restart is fine), the
/// one-device-first rule for changes, answers that fit and mask secrets, an audit without the body.
/// </summary>
public sealed class DeviceRequestToolTests
{
    private static string LibraryDirectory => Path.Combine(AppContext.BaseDirectory, "Library");

    private static async Task<(SdkTesting.FakeCoreContext Ctx, VapixCommanderPlugin Plugin, FakeTimeProvider Time)> StartAsync()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var plugin = new VapixCommanderPlugin(LibraryDirectory) { Time = time };
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (ctx, plugin, time);
    }

    /// <summary>The payload the host hands the plugin: the arguments with the filter resolved to device ids.</summary>
    private static string Payload(JsonObject args, params SdkTesting.FakeDevice[] devices)
    {
        var payload = (JsonObject)args.DeepClone();
        payload["device_ids"] = new JsonArray([.. devices.Select(d => (JsonNode)JsonValue.Create(d.Id.ToString())!)]);
        payload["devices_total"] = devices.Length;
        return payload.ToJsonString();
    }

    private static async Task<JsonObject> CallAsync(SdkTesting.FakeCoreContext ctx, VapixCommanderPlugin plugin, ICallContext call, string method, JsonObject args, params SdkTesting.FakeDevice[] devices) =>
        JsonNode.Parse((await ctx.InvokeAsync(plugin, call, method, Payload(args, devices)))!)!.AsObject();

    private static Task<JsonObject> PreviewAsync(SdkTesting.FakeCoreContext ctx, VapixCommanderPlugin plugin, ICallContext call, JsonObject args, params SdkTesting.FakeDevice[] devices) =>
        CallAsync(ctx, plugin, call, DeviceRequestTool.Request.PreviewMethod, args, devices);

    private static Task<JsonObject> RunAsync(SdkTesting.FakeCoreContext ctx, VapixCommanderPlugin plugin, ICallContext call, JsonObject args, params SdkTesting.FakeDevice[] devices) =>
        CallAsync(ctx, plugin, call, DeviceRequestTool.Request.RunMethod, args, devices);

    private static string? FirstError(JsonObject answer) =>
        answer["errors"] is JsonArray errors ? errors[0]!["field"]!.GetValue<string>() + ": " + errors[0]!["error"]!.GetValue<string>() : null;

    private static JsonObject Brightness() => new()
    {
        ["method"] = "POST",
        ["path"] = "/axis-cgi/param.cgi",
        ["body"] = "action=update&Image.I0.Appearance.Brightness=60",
        ["content_type"] = "application/x-www-form-urlencoded",
    };

    private static JsonObject ListBrand() => new()
    {
        ["method"] = "GET",
        ["path"] = "/axis-cgi/param.cgi",
        ["query"] = new JsonArray(new JsonObject { ["name"] = "action", ["value"] = "list" }, new JsonObject { ["name"] = "group", ["value"] = "Brand" }),
    };

    private static (SdkTesting.FakeDevice Device, SdkTesting.FakeCamera Camera) AddDevice(SdkTesting.FakeCoreContext ctx, int n)
    {
        var camera = new SdkTesting.FakeCamera();
        var device = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2." + n, HostName = "cam" + n }, camera);
        return (device, camera);
    }

    [Fact]
    public void The_tool_is_oadm_device_request_with_the_right_send_and_a_dangerous_right_operators_do_not_get()
    {
        var plugin = new VapixCommanderPlugin(LibraryDirectory);
        var tool = Assert.Single(plugin.Tools, t => t.Name == "request");
        Assert.Equal("device", tool.Area);
        Assert.True(tool.Changes && tool.DeviceFilter);
        Assert.Equal("send", tool.Permission);
        Assert.Equal("send", plugin.RequiredPermission(tool.PreviewMethod));
        Assert.Equal("send", plugin.RequiredPermission(tool.RunMethod));
        var dangerous = Assert.Single(plugin.Permissions, p => p.Key == DeviceRequestTool.DangerousPermission);
        Assert.False(dangerous.OperatorDefault);
        Assert.True(dangerous.FullDeviceControl);
        Assert.True(plugin.IsAudited(tool.RunMethod));
        Assert.False(plugin.IsAudited(tool.PreviewMethod));
    }

    [Fact]
    public async Task The_preview_says_read_or_change_shows_the_request_and_sends_nothing()
    {
        var (ctx, plugin, _) = await StartAsync();
        var (device, camera) = AddDevice(ctx, 10);
        var call = ctx.CallAsAllDevices();

        var read = await PreviewAsync(ctx, plugin, call, ListBrand(), device);
        Assert.Equal("read", read["kind"]!.GetValue<string>());
        Assert.Equal("GET /axis-cgi/param.cgi?action=list&group=Brand", read["request"]!.GetValue<string>());
        Assert.Equal(1, read["will_run"]!.GetValue<int>());
        Assert.Equal(0, read["will_change"]!.GetValue<int>());
        Assert.Empty(read["warnings"]!.AsArray());

        var change = await PreviewAsync(ctx, plugin, call, Brightness(), device);
        Assert.Equal("change", change["kind"]!.GetValue<string>());
        Assert.Equal(1, change["will_change"]!.GetValue<int>());
        Assert.Equal("device", change["unit"]!.GetValue<string>());
        Assert.False(change["dangerous"]!.GetValue<bool>());
        Assert.Equal(DeviceRequestTool.ChangeWarning, Assert.Single(change["warnings"]!.AsArray())!.GetValue<string>());
        Assert.Contains("Brightness=60", change["body"]!.GetValue<string>(), StringComparison.Ordinal);

        Assert.Empty(camera.Requests); // a preview never reaches the device
    }

    [Fact]
    public async Task A_run_sends_the_request_once_per_device_and_answers_status_and_body_with_secrets_masked()
    {
        var (ctx, plugin, _) = await StartAsync();
        var (a, cameraA) = AddDevice(ctx, 10);
        var (b, cameraB) = AddDevice(ctx, 11);
        foreach (var camera in new[] { cameraA, cameraB })
        {
            camera.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("root.Brand.Brand=AXIS\nroot.SNMP.V2CommunityRead=s3cretCommunity\nroot.MQTT.Client.Password=hunter22\n", Encoding.UTF8, "text/plain"),
            };
        }

        var answer = await RunAsync(ctx, plugin, ctx.CallAsAllDevices(), ListBrand(), a, b);

        Assert.Null(FirstError(answer));
        Assert.Equal(2, answer["sent"]!.GetValue<int>());
        Assert.Equal(2, answer["succeeded"]!.GetValue<int>());
        Assert.Single(cameraA.Requests, r => r == "GET axis-cgi/param.cgi?action=list&group=Brand");
        Assert.Single(cameraB.Requests, r => r.StartsWith("GET axis-cgi/param.cgi", StringComparison.Ordinal));
        var row = answer["devices"]![0]!.AsObject();
        Assert.Equal(200, row["status"]!.GetValue<int>());
        Assert.True(row["ok"]!.GetValue<bool>());
        var body = row["body"]!.GetValue<string>();
        Assert.Contains("root.Brand.Brand=AXIS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cretCommunity", answer.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter22", answer.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("root.MQTT.Client.Password=***", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_and_xml_answers_mask_values_whose_names_point_to_a_secret_but_never_a_yes_no_value()
    {
        var json = DeviceRequestTool.MaskText("""{"data":{"user":"root","password":"hunter22","passwordEnabled":true,"items":[{"apiKey":"abcd1234"}]}}""", null);
        Assert.DoesNotContain("hunter22", json, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd1234", json, StringComparison.Ordinal);
        Assert.Contains("\"user\": \"root\"", json, StringComparison.Ordinal);
        Assert.Contains("true", json, StringComparison.Ordinal);

        var xml = DeviceRequestTool.MaskText("<Settings><Community>publicx1</Community><Enabled>yes</Enabled><Auth token=\"t0ken99\"/></Settings>", null);
        Assert.Contains("<Community>***</Community>", xml, StringComparison.Ordinal);
        Assert.Contains("<Enabled>yes</Enabled>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("t0ken99", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_runs_on_more_devices_only_after_it_succeeded_on_one_for_the_same_caller_and_not_after_a_day()
    {
        var (ctx, plugin, time) = await StartAsync();
        var (a, cameraA) = AddDevice(ctx, 10);
        var (b, cameraB) = AddDevice(ctx, 11);
        var anna = ctx.CallAsAllDevices("anna");

        // Two devices first: refused, nothing sent.
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await PreviewAsync(ctx, plugin, anna, Brightness(), a, b)));
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await RunAsync(ctx, plugin, anna, Brightness(), a, b)));
        Assert.Empty(cameraA.Requests);
        Assert.Empty(cameraB.Requests);

        // A failed run on one device unlocks nothing.
        cameraA.Status = HttpStatusCode.InternalServerError;
        var failed = await RunAsync(ctx, plugin, anna, Brightness(), a);
        Assert.Equal(1, failed["failed"]!.GetValue<int>());
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await PreviewAsync(ctx, plugin, anna, Brightness(), a, b)));

        // A run that succeeded on one device opens the same request for more devices.
        cameraA.Status = HttpStatusCode.OK;
        var one = await RunAsync(ctx, plugin, anna, Brightness(), a);
        Assert.Equal(1, one["succeeded"]!.GetValue<int>());
        Assert.Null(FirstError(await PreviewAsync(ctx, plugin, anna, Brightness(), a, b)));
        var both = await RunAsync(ctx, plugin, anna, Brightness(), a, b);
        Assert.Equal(2, both["succeeded"]!.GetValue<int>());
        Assert.Contains(cameraB.Requests, r => r == "POST axis-cgi/param.cgi");

        // Not another value, not another caller, and not after a day.
        var other = Brightness();
        other["body"] = "action=update&Image.I0.Appearance.Brightness=70";
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await PreviewAsync(ctx, plugin, anna, other, a, b)));
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await PreviewAsync(ctx, plugin, ctx.CallAsAllDevices("bob"), Brightness(), a, b)));
        time.Advance(DeviceRequestTrials.Lifetime);
        Assert.Equal("filter: " + DeviceRequestTool.OneDeviceFirst, FirstError(await PreviewAsync(ctx, plugin, anna, Brightness(), a, b)));
    }

    [Fact]
    public async Task Reads_run_on_many_devices_at_once()
    {
        var (ctx, plugin, _) = await StartAsync();
        var devices = Enumerable.Range(10, 5).Select(n => AddDevice(ctx, n).Device).ToArray();

        var answer = await RunAsync(ctx, plugin, ctx.CallAsAllDevices(), ListBrand(), devices);

        Assert.Equal(5, answer["sent"]!.GetValue<int>());
    }

    public static TheoryData<string, string, string?, string?> LockOutRequests => new()
    {
        { "POST", "/axis-cgi/pwdgrp.cgi", "action=remove&user=root", "application/x-www-form-urlencoded" },
        { "POST", "/axis-cgi/network_settings.cgi", """{"apiVersion":"1.0","method":"setIPv4AddressConfiguration","params":{"deviceName":"eth0","configurationMode":"static"}}""", null },
        { "POST", "/axis-cgi/param.cgi", "action=update&Network.eth0.IPAddress=10.9.9.9", "application/x-www-form-urlencoded" },
        { "POST", "/axis-cgi/param.cgi", "action=update&HTTPS.Port=8443", "application/x-www-form-urlencoded" },
        { "POST", "/axis-cgi/param.cgi", "action=update&root.IEEE8021X.Enabled=no", "application/x-www-form-urlencoded" },
        { "POST", "/axis-cgi/factorydefault.cgi", null, null },
        { "POST", "/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"upgrade"}""", null },
        { "PATCH", "/config/rest/network-settings/v1/ipv4", """{"data":{"dhcp":false}}""", null },
        { "PUT", "/config/rest/web-server/v1/https", """{"data":{"enabled":false}}""", null },
        { "POST", "/config/rest/ieee8021x/v1/settings", """{"data":{"enabled":false}}""", null },
    };

    [Theory]
    [MemberData(nameof(LockOutRequests))]
    public async Task A_request_that_can_lock_OADM_out_needs_the_dangerous_right_and_then_warns(string method, string path, string? body, string? contentType)
    {
        var (ctx, plugin, _) = await StartAsync();
        var (device, camera) = AddDevice(ctx, 10);
        var args = new JsonObject { ["method"] = method, ["path"] = path, ["body"] = body, ["content_type"] = contentType };

        var refused = await PreviewAsync(ctx, plugin, new Rights(ctx.CallAsAllDevices("op"), dangerous: false), args, device);
        Assert.Equal("path: " + DeviceRequestTool.DangerousRefused, FirstError(refused));
        Assert.Equal("path: " + DeviceRequestTool.DangerousRefused, FirstError(await RunAsync(ctx, plugin, new Rights(ctx.CallAsAllDevices("op"), dangerous: false), args, device)));

        var allowed = await PreviewAsync(ctx, plugin, new Rights(ctx.CallAsAllDevices("admin"), dangerous: true), args, device);
        Assert.Null(FirstError(allowed));
        Assert.True(allowed["dangerous"]!.GetValue<bool>());
        Assert.Equal(DeviceRequestTool.DangerousWarning, Assert.Single(allowed["warnings"]!.AsArray())!.GetValue<string>());
        Assert.Empty(camera.Requests);
    }

    [Fact]
    public async Task A_restart_needs_no_dangerous_right_and_warns_that_the_device_is_offline()
    {
        var (ctx, plugin, _) = await StartAsync();
        var (device, camera) = AddDevice(ctx, 10);
        var call = new Rights(ctx.CallAsAllDevices("op"), dangerous: false);
        var args = new JsonObject { ["method"] = "GET", ["path"] = "/axis-cgi/restart.cgi" };

        var plan = await PreviewAsync(ctx, plugin, call, args, device);
        Assert.False(plan["dangerous"]!.GetValue<bool>());
        Assert.Equal("change", plan["kind"]!.GetValue<string>());
        Assert.Equal(DeviceRequestTool.RestartWarning, Assert.Single(plan["warnings"]!.AsArray())!.GetValue<string>());

        var run = await RunAsync(ctx, plugin, call, args, device);
        Assert.Equal(1, run["sent"]!.GetValue<int>());
        Assert.Contains(camera.Requests, r => r == "GET axis-cgi/restart.cgi");
    }

    [Theory]
    [InlineData("/local/app/x.cgi", "path: " + DeviceRequestTool.PathPrefix)]
    [InlineData("axis-cgi/param.cgi", "path: " + DeviceRequestTool.PathPrefix)]
    [InlineData("/axis-cgi/param.cgi?action=list", "path: " + DeviceRequestTool.PathQuery)]
    [InlineData("/axis-cgi/../local/x.cgi", "path: " + CommandValidator.PathProblem)]
    [InlineData("/axis-cgi/%2e%2e/restart.cgi", "path: " + CommandValidator.PathProblem)]
    [InlineData("/axis-cgi/{{x}}.cgi", "path: " + DeviceRequestTool.NoPlaceholders)]
    public async Task Paths_off_the_device_APIs_are_refused(string path, string error)
    {
        var (ctx, plugin, _) = await StartAsync();
        var (device, camera) = AddDevice(ctx, 10);

        Assert.Equal(error, FirstError(await PreviewAsync(ctx, plugin, ctx.CallAsAllDevices(), new JsonObject { ["method"] = "GET", ["path"] = path }, device)));
        Assert.Empty(camera.Requests);
    }

    [Fact]
    public async Task Wrong_input_and_a_password_in_the_request_are_refused_per_field()
    {
        var (ctx, plugin, _) = await StartAsync();
        var (device, _) = AddDevice(ctx, 10);
        var call = ctx.CallAsAllDevices();

        async Task<string?> Error(JsonObject args) => FirstError(await PreviewAsync(ctx, plugin, call, args, device));

        Assert.Equal("method: " + DeviceRequestTool.MethodText, await Error(new JsonObject { ["method"] = "HEAD", ["path"] = "/axis-cgi/param.cgi" }));
        Assert.Equal("body: " + DeviceRequestTool.GetBody, await Error(new JsonObject { ["method"] = "GET", ["path"] = "/axis-cgi/param.cgi", ["body"] = "x=1" }));
        Assert.Equal("body: " + DeviceRequestTool.InvalidJson, await Error(new JsonObject { ["method"] = "POST", ["path"] = "/axis-cgi/basicdeviceinfo.cgi", ["body"] = "{broken" }));
        Assert.Equal("content_type: " + DeviceRequestTool.ContentTypeText, await Error(new JsonObject { ["method"] = "POST", ["path"] = "/axis-cgi/x.cgi", ["body"] = "a", ["content_type"] = "json please" }));
        Assert.Equal("query: " + DeviceRequestTool.SecretInRequest, await Error(new JsonObject
        {
            ["method"] = "GET",
            ["path"] = "/axis-cgi/pwdgrp.cgi",
            ["query"] = new JsonArray(new JsonObject { ["name"] = "action", ["value"] = "add" }, new JsonObject { ["name"] = "pwd", ["value"] = "Secret123" }),
        }));
        Assert.Equal("body: " + DeviceRequestTool.SecretInRequest, await Error(new JsonObject
        {
            ["method"] = "POST",
            ["path"] = "/axis-cgi/mqtt/client.cgi",
            ["body"] = """{"apiVersion":"1.0","method":"configureClient","params":{"server":{"host":"x"},"password":"hunter22"}}""",
        }));
        Assert.Equal("filter: " + DeviceRequestTool.NoDevices, FirstError(await PreviewAsync(ctx, plugin, call, ListBrand())));

        var many = JsonNode.Parse(Payload(ListBrand(), device))!.AsObject();
        many["devices_total"] = DeviceRequestTool.MaxDevices + 1;
        var tooMany = JsonNode.Parse((await ctx.InvokeAsync(plugin, call, DeviceRequestTool.Request.PreviewMethod, many.ToJsonString()))!)!.AsObject();
        Assert.Equal("filter: " + DeviceRequestTool.TooMany(DeviceRequestTool.MaxDevices + 1), FirstError(tooMany));
    }

    [Fact]
    public async Task Large_answers_are_cut_so_the_whole_answer_stays_under_32_KB()
    {
        var (ctx, plugin, _) = await StartAsync();
        var big = "{\"data\":[" + string.Join(",", Enumerable.Range(0, 20_000).Select(i => "{\"n\":" + i + ",\"text\":\"äöü line\"}")) + "]}";
        var devices = Enumerable.Range(10, 12).Select(n =>
        {
            var (device, camera) = AddDevice(ctx, n);
            camera.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big, Encoding.UTF8, "application/json") };
            return device;
        }).ToArray();

        var answer = await RunAsync(ctx, plugin, ctx.CallAsAllDevices(), new JsonObject { ["method"] = "GET", ["path"] = "/axis-cgi/listthings.cgi" }, devices);

        Assert.True(Encoding.UTF8.GetByteCount(answer.ToJsonString()) < 32 * 1024, "the answer fits the host's 32 KB");
        Assert.Equal(12, answer["sent"]!.GetValue<int>());
        Assert.True(answer["devices"]![0]!["body_cut"]!.GetValue<bool>());
        Assert.Null(answer["devices"]![11]!["body"]); // bodies of the first devices only
        Assert.NotNull(answer["note"]);
    }

    [Fact]
    public void The_audit_names_method_path_and_devices_but_never_the_query_values_or_the_body()
    {
        var args = Brightness();
        args["query"] = new JsonArray(new JsonObject { ["name"] = "camera", ["value"] = "SecretQueryValue" });
        var payload = Payload(args, new SdkTesting.FakeDevice(), new SdkTesting.FakeDevice());
        var plugin = new VapixCommanderPlugin(LibraryDirectory);

        var detail = plugin.AuditDetail(DeviceRequestTool.Request.RunMethod, payload);

        Assert.Equal("Device request POST /axis-cgi/param.cgi on 2 devices", detail);
        Assert.Null(plugin.AuditDetail(CommanderMethods.TryRequest, payload));
    }

    /// <summary>A caller with or without the plugin's right "dangerous"; everything else as the inner call.</summary>
    private sealed class Rights(ICallContext inner, bool dangerous) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public IDeviceRepository Devices => inner.Devices;

        public IVapixClientFactory Vapix => inner.Vapix;

        public ITaskRunner Tasks => inner.Tasks;

        public IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) =>
            permission == PermissionKeys.Of(VapixCommanderPlugin.PluginId, DeviceRequestTool.DangerousPermission) ? dangerous : inner.Has(permission);

        public bool Contains(IDeviceInfo device) => inner.Contains(device);
    }
}
