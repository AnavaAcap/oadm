using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Plugins.DeviceConfig.Tools;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// The read-only tools of Device configuration (README "Tools"): settings grouped by value, the label search, secrets never
/// answered, the caller's device view, devices that cannot be read, the time budget and the answer size. Cameras answer from
/// the recorded fixtures of 10.0.0.48 (<see cref="ConfigureCamera"/>); no real device.
/// </summary>
public sealed class ConfigToolsTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string CaptureFrequency = "ImageSource.I0.CaptureFrequency";

    /// <summary>Devices and their cameras for the call.</summary>
    private sealed class Fleet : IDeviceRepository, IVapixClientFactory
    {
        public List<FakeDevice> Devices { get; } = [];

        public Dictionary<Guid, IVapixClient> Clients { get; } = [];

        public FakeDevice Add(string address, IVapixClient? client = null, DeviceStatus status = DeviceStatus.Ok, DeviceCategory category = DeviceCategory.Camera)
        {
            var device = new FakeDevice { Address = address, HostName = "cam-" + address.Split('.')[^1], Status = status, Category = category };
            Devices.Add(device);
            Clients[device.Id] = client ?? new ConfigureCamera();
            return device;
        }

        public ConfigureCamera Camera(FakeDevice device) => (ConfigureCamera)Clients[device.Id];

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<IDeviceInfo>>([.. Devices]);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult<IDeviceInfo?>(Devices.Find(d => d.Id == id));

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(Clients[deviceId]);

        /// <summary>The payload the host hands the plugin for these devices.</summary>
        public string Payload(JsonObject args, IEnumerable<FakeDevice>? devices = null, int? total = null)
        {
            var chosen = (devices ?? Devices).ToList();
            args["device_ids"] = new JsonArray([.. chosen.Select(d => (JsonNode)JsonValue.Create(d.Id.ToString())!)]);
            args["devices_total"] = total ?? chosen.Count;
            return args.ToJsonString();
        }
    }

    private static async Task<JsonObject> CallAsync(ICallContext caller, PluginTool tool, string payload, TimeProvider? time = null) =>
        JsonNode.Parse((await ConfigTools.InvokeAsync(caller, tool.Method, payload, time ?? TimeProvider.System, CancellationToken.None))!)!.AsObject();

    private static Fleet SevenCameras()
    {
        var fleet = new Fleet();
        for (var i = 1; i <= 6; i++)
        {
            fleet.Add("10.0.0." + (10 + i));
        }

        var fifty = fleet.Add("10.0.0.24");
        fleet.Camera(fifty).Values[CaptureFrequency] = "50Hz";
        return fleet;
    }

    [Fact]
    public async Task A_setting_found_by_its_label_is_grouped_by_value_with_device_counts_and_names()
    {
        var fleet = SevenCameras();
        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.Parameters, fleet.Payload(new JsonObject { ["search"] = "capture frequency" }));
        output.WriteLine(answer.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal((7, 7, 0), (answer["devices"]!.GetValue<int>(), answer["read"]!.GetValue<int>(), answer["not_read"]!.GetValue<int>()));
        var row = answer["parameters"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == CaptureFrequency)!;
        Assert.Equal("Capture frequency", row["label"]!.GetValue<string>());
        var values = row["values"]!.AsArray();
        Assert.Equal(("60Hz", "60 Hz", 6), (values[0]!["value"]!.GetValue<string>(), values[0]!["text"]!.GetValue<string>(), values[0]!["count"]!.GetValue<int>()));
        Assert.Equal(("50Hz", "50 Hz", 1), (values[1]!["value"]!.GetValue<string>(), values[1]!["text"]!.GetValue<string>(), values[1]!["count"]!.GetValue<int>()));
        Assert.Equal("10.0.0.24", Assert.Single(values[1]!["devices"]!.AsArray())!["address"]!.GetValue<string>());
        Assert.Equal(ConfigTools.MaxNamed, values[0]!["devices"]!.AsArray().Count); // a few names, the count says how many
        Assert.Equal(0, row["missing"]!.GetValue<int>());
        Assert.Empty(answer["failed"]!.AsArray());
    }

    [Theory]
    [InlineData("CaptureFrequency")]
    [InlineData("CAPTURE-FREQUENCY")]
    [InlineData("imagesource.i0.capturefrequency")]
    public async Task The_search_matches_the_raw_name_and_the_label_in_any_case(string search)
    {
        DeviceSettingsReader.ClearCache();
        var fleet = new Fleet();
        fleet.Add("10.0.0.11");
        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.Parameters, fleet.Payload(new JsonObject { ["search"] = search }));
        Assert.Contains(answer["parameters"]!.AsArray(), p => p!["name"]!.GetValue<string>() == CaptureFrequency);
    }

    [Fact]
    public async Task Exact_names_read_only_their_group_and_report_the_devices_without_the_setting()
    {
        var fleet = new Fleet();
        var first = fleet.Add("10.0.0.11");
        var other = fleet.Add("10.0.0.12");
        fleet.Camera(other).Values[CaptureFrequency] = "50Hz";

        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.Parameters,
            fleet.Payload(new JsonObject { ["names"] = new JsonArray("root." + CaptureFrequency) }));

        var row = Assert.Single(answer["parameters"]!.AsArray())!;
        Assert.Equal(["50Hz", "60Hz"], row["values"]!.AsArray().Select(v => v!["value"]!.GetValue<string>()).Order(StringComparer.Ordinal));
        var definitions = fleet.Camera(first).Requests.Where(r => r.Contains("listdefinitions", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(definitions);
        Assert.All(definitions, r => Assert.Contains("group=ImageSource", r, StringComparison.Ordinal)); // never the whole tree

        // A camera without the setting (another model or an older AXIS OS) is counted and named.
        var with = new FakeDevice { Address = "10.0.0.21" };
        var without = new FakeDevice { Address = "10.0.0.22" };
        var round = new ReadRound(
        [
            new DeviceRead(with, new DeviceSettings { Parameters = [new ParamDefinition(CaptureFrequency, ParamKind.Enum) { Value = "60Hz" }] }),
            new DeviceRead(without, new DeviceSettings()),
        ], 2, 0);
        var missing = Assert.Single(ConfigTools.ParametersOf(round, ConfigTools.ParameterQuery.Of(new ConfigTools.ParametersArgs(Names: [CaptureFrequency]))).Parameters);
        Assert.Equal(1, missing.Missing);
        Assert.Equal("10.0.0.22", Assert.Single(missing.MissingOn).Address);
    }

    [Theory]
    [InlineData("community")]
    [InlineData("password")]
    [InlineData("V1ReadCommunity")]
    public async Task Passwords_and_secret_parameters_are_never_answered(string search)
    {
        var fleet = new Fleet();
        var device = fleet.Add("10.0.0.11");
        fleet.Camera(device).Values["SNMP.V1ReadCommunity"] = "s3cret-community";
        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.Parameters, fleet.Payload(new JsonObject { ["search"] = search }));

        var text = answer.ToJsonString();
        Assert.DoesNotContain("s3cret-community", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SNMP.V1ReadCommunity", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ProxyPassword", text, StringComparison.Ordinal);
        Assert.DoesNotContain(answer["parameters"]!.AsArray(), p => SecretNames.IsSecret(p!["name"]!.GetValue<string>()));
        Assert.Contains(ConfigTools.SecretNote, answer["note"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_devices_of_the_callers_view_are_read_and_an_id_outside_refuses_the_call()
    {
        var fleet = SevenCameras();
        var outside = fleet.Devices[^1];
        var inside = fleet.Devices.Take(6).ToList();
        var group = FakeCallContext.OverGroup(inside.Select(d => d.Id), fleet, fleet);

        var answer = await CallAsync(group, ConfigTools.Parameters, fleet.Payload(new JsonObject { ["names"] = new JsonArray(CaptureFrequency) }, inside));
        Assert.Equal(6, answer["read"]!.GetValue<int>());
        Assert.Equal("60Hz", Assert.Single(Assert.Single(answer["parameters"]!.AsArray())!["values"]!.AsArray())!["value"]!.GetValue<string>());

        // An id outside the group answers like an unknown one, before any device is asked.
        foreach (var tool in ConfigTools.All)
        {
            var refused = await Assert.ThrowsAsync<DevicesNotAvailableException>(() =>
                ConfigTools.InvokeAsync(group, tool.Method, fleet.Payload(new JsonObject { ["names"] = new JsonArray(CaptureFrequency) }), TimeProvider.System, CancellationToken.None));
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), refused.Message);
        }

        Assert.Empty(fleet.Camera(outside).Requests); // never asked
    }

    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new FakeCoreContext();
        var inside = ctx.Add(new Oadm.Sdk.Testing.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new Oadm.Sdk.Testing.FakeDevice { Address = "10.0.0.49" });
        var plugin = new DeviceConfigToolsPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(ConfigTools.Parameters.Method, """{"search":"capture frequency","device_ids":["{inside}","{device}"],"devices_total":2}""")
            .Method(ConfigTools.ActionRules.Method, """{"device_ids":["{device}"],"devices_total":1}""")
            .Method(ConfigTools.StreamProfiles.Method, """{"device_ids":["{inside}","{device}"],"devices_total":2}""")
            .RunAsync();
    }

    [Fact]
    public async Task Devices_that_cannot_be_read_are_reported_by_reason()
    {
        var fleet = new Fleet();
        fleet.Add("10.0.0.11");
        var offline = fleet.Add("10.0.0.12", status: DeviceStatus.Unreachable);
        var login = fleet.Add("10.0.0.13", status: DeviceStatus.CredentialsRequired);
        var refused = fleet.Add("10.0.0.14");
        fleet.Camera(refused).Fail = _ => new HttpRequestException("Connection refused");

        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.Parameters, fleet.Payload(new JsonObject { ["search"] = "capture frequency" }));

        Assert.Equal(1, answer["read"]!.GetValue<int>());
        var failed = answer["failed"]!.AsArray().ToDictionary(f => f!["reason"]!.GetValue<string>(), f => f!["devices"]!.AsArray().Single()!["address"]!.GetValue<string>());
        Assert.Equal("10.0.0.12", failed[TaskSupportReasons.ForStatus(offline)!]);
        Assert.Equal("10.0.0.13", failed[TaskSupportReasons.ForStatus(login)!]);
        Assert.Equal("10.0.0.14", failed[DeviceMessages.Unreachable("Connection refused")]);
        Assert.Empty(fleet.Camera(offline).Requests); // refused without a request
    }

    /// <summary>A camera that answers only when its call is cancelled.</summary>
    private sealed class SilentCamera : IVapixClient
    {
        private int _waiting;

        public int Waiting => Volatile.Read(ref _waiting);

        public Uri BaseAddress { get; } = new("https://10.0.0.99/");

        public async Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _waiting);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("never");
        }

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new InvalidOperationException("not used");

        public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("not used");

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new InvalidOperationException("not used");

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new InvalidOperationException("not used");
    }

    [Fact]
    public async Task A_call_answers_after_its_time_budget_with_what_it_read_and_counts_the_rest_as_not_read()
    {
        var fleet = new Fleet();
        fleet.Add("10.0.0.11");
        fleet.Add("10.0.0.12");
        var silent = new SilentCamera();
        fleet.Add("10.0.0.13", silent);
        fleet.Add("10.0.0.14", silent);
        var time = new FakeTimeProvider();
        var read = 0;

        var call = ToolDeviceReads.ReadAsync(FakeCallContext.Over(fleet, fleet), PluginToolDevices.Of(fleet.Payload([], total: 10)), async (vapix, device, ct) =>
        {
            var settings = await DeviceSettingsReader.ReadParametersAsync(vapix, device, p => p.Name == CaptureFrequency, [CaptureFrequency], ct);
            Interlocked.Increment(ref read);
            return settings;
        }, time, CancellationToken.None);

        await Eventually.TrueAsync(() => Volatile.Read(ref read) == 2 && silent.Waiting == 2, "the answering cameras read and the silent ones waiting");
        Assert.False(call.IsCompleted);
        time.Advance(ToolDeviceReads.Budget);
        var round = await call.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, round.Reads.Count);
        Assert.Equal(2 + 6, round.NotRead); // the two silent ones and the six beyond the devices handed over
        var answer = ConfigTools.ParametersOf(round, ConfigTools.ParameterQuery.Of(new ConfigTools.ParametersArgs(Names: [CaptureFrequency])));
        Assert.Equal((10, 2, 8), (answer.Devices, answer.Read, answer.NotRead));
        Assert.StartsWith("8 devices were not read.", answer.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_over_many_devices_and_settings_stays_within_the_size_limit()
    {
        var reads = Enumerable.Range(0, PluginTool.MaxDevices).Select(i =>
        {
            var device = new FakeDevice { Address = "10.1." + (i / 250) + "." + (i % 250), HostName = new string('h', 60) + i };
            var parameters = Enumerable.Range(0, 60).Select(p => new ParamDefinition("Group.P" + p, ParamKind.Text) { NiceName = "Setting " + p, Value = new string('v', 150) + i }).ToList();
            return new DeviceRead(device, new DeviceSettings { Parameters = parameters });
        }).ToList();

        var answer = ConfigTools.ParametersOf(new ReadRound(reads, 20_000, 20_000 - reads.Count),
            ConfigTools.ParameterQuery.Of(new ConfigTools.ParametersArgs(Search: "setting")));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(answer, ConfigTools.Json).Length;
        Assert.True(bytes <= ConfigTools.MaxAnswerBytes, bytes + " bytes");
        Assert.InRange(answer.Parameters.Count, 1, ConfigTools.MaxRows);
        Assert.Equal(60, answer.ParametersTotal);
        Assert.All(answer.Parameters, p => Assert.True(p.Values.Count <= ConfigTools.MaxValues && p.OtherValues > 0));
    }

    [Fact]
    public async Task Action_rules_are_grouped_by_rule_with_the_event_in_words_and_no_password()
    {
        var fleet = new Fleet();
        var first = fleet.Add("10.0.0.11");
        var second = fleet.Add("10.0.0.12");
        var camera = fleet.Camera(first);
        camera.Configurations.Add(new ConfigAction("Upload", "com.axis.action.fixed.ftp", [new("host", "ftp.example"), new("password", "hunter2"), new("token", "abc123")]) { Id = "77" });
        camera.Rules.Add(new ConfigureCamera.DeviceRule("78", "Upload on pulse", "77", camera.Rules[0].StartEventXml));

        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.ActionRules, fleet.Payload([]));

        var expected = fleet.Camera(second).Rules.Count;
        var rules = answer["rules"]!.AsArray();
        Assert.Equal(expected + 1, answer["rules_total"]!.GetValue<int>());
        Assert.All(rules.Take(expected), r => Assert.Equal(2, r!["count"]!.GetValue<int>())); // the rules both cameras have, first
        var upload = rules.Single(r => r!["name"]!.GetValue<string>() == "Upload on pulse")!;
        Assert.Equal((1, "Upload", "com.axis.action.fixed.ftp"), (upload["count"]!.GetValue<int>(), upload["action"]!.GetValue<string>(), upload["action_type"]!.GetValue<string>()));
        Assert.Equal("Recurring pulse", upload["event"]!.GetValue<string>()); // tns1:UserAlarm/tnsaxis:Recurring/Pulse as the dialog shows it
        Assert.Equal(["host"], upload["settings"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()));
        var text = answer.ToJsonString();
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", text, StringComparison.Ordinal);
        Assert.Contains(ConfigTools.SecretNote, answer["note"]!.GetValue<string>(), StringComparison.Ordinal);

        // Which devices have a rule named X.
        var named = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.ActionRules, fleet.Payload(new JsonObject { ["name"] = "upload" }));
        Assert.Equal("10.0.0.11", Assert.Single(Assert.Single(named["rules"]!.AsArray())!["devices"]!.AsArray())!["address"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_profiles_are_grouped_by_name_and_devices_without_video_are_counted()
    {
        var fleet = new Fleet();
        var first = fleet.Add("10.0.0.11");
        var second = fleet.Add("10.0.0.12");
        fleet.Camera(second).Profiles[0] = fleet.Camera(second).Profiles[0] with { Parameters = "resolution=640x480" };
        var io = fleet.Add("10.0.0.13", category: DeviceCategory.IoModule);

        var answer = await CallAsync(FakeCallContext.Over(fleet, fleet), ConfigTools.StreamProfiles, fleet.Payload([]));

        Assert.Equal((3, 2, 1), (answer["profiles_total"]!.GetValue<int>(), answer["read"]!.GetValue<int>(), answer["no_video"]!.GetValue<int>()));
        var high = answer["profiles"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == "View Area 1_ACS_Pro_High")!;
        Assert.Equal(2, high["count"]!.GetValue<int>());
        Assert.True(high["parameters_differ"]!.GetValue<bool>());
        Assert.Contains("resolution=1920x1080", high["parameters"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(fleet.Camera(io).Requests);
        _ = first;
    }

    [Fact]
    public async Task A_role_without_Configure_devices_is_refused_and_invalid_input_names_the_field()
    {
        var fleet = SevenCameras();
        var all = FakeCallContext.Over(fleet, fleet);
        var refused = new RoleCall(all, []);
        foreach (var tool in ConfigTools.All)
        {
            await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ConfigTools.InvokeAsync(refused, tool.Method, fleet.Payload(new JsonObject { ["search"] = "capture" }), TimeProvider.System, CancellationToken.None));
        }

        Assert.All(fleet.Devices, d => Assert.Empty(fleet.Camera(d).Requests));

        // The dangerous right alone opens the dialog's reads too.
        var dangerous = new RoleCall(all, [PermissionKeys.Task(ConfigureIds.Configure, ConfigurePermissions.Dangerous)]);
        Assert.NotNull(await ConfigTools.InvokeAsync(dangerous, ConfigTools.Parameters.Method, fleet.Payload(new JsonObject { ["names"] = new JsonArray(CaptureFrequency) }), TimeProvider.System, CancellationToken.None));

        foreach (var bad in new[] { new JsonObject(), new JsonObject { ["search"] = "ca" }, new JsonObject { ["search"] = "capture", ["names"] = new JsonArray(CaptureFrequency) } })
        {
            var errors = await CallAsync(all, ConfigTools.Parameters, fleet.Payload(bad));
            Assert.Single(errors["errors"]!.AsArray());
        }
    }

    [Fact]
    public async Task The_plugin_offers_the_three_tools_under_config_and_needs_the_users_call()
    {
        var plugin = new DeviceConfigToolsPlugin();
        Assert.Equal(("oadm.device-config", "config", false), (plugin.Id, plugin.ToolArea, plugin.HasPage));
        Assert.Equal(["parameters", "action_rules", "stream_profiles"], plugin.Tools.Select(t => t.Name));
        Assert.All(plugin.Tools, t => Assert.True(!t.Changes && t.DeviceFilter && t.TaskPlugin == ConfigureIds.Configure, t.Name));
        Assert.Equal(["Reading settings", "Reading action rules", "Reading stream profiles"], plugin.Tools.Select(t => t.Title));
        await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.InvokeAsync(ConfigTools.Parameters.Method, "{}", CancellationToken.None));
        var fleet = new Fleet();
        await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(FakeCallContext.Over(fleet, fleet), "other", "{}", CancellationToken.None));
    }

    [Fact]
    public void Every_input_and_answer_has_a_schema_the_host_offers()
    {
        foreach (var tool in ConfigTools.All)
        {
            Assert.True(Oadm.Server.Tasks.TaskPayloadSchemas.Build(tool.InputType!, tool.JsonOptions) is not null, tool.Name + " input");
            Assert.True(Oadm.Server.Tasks.TaskPayloadSchemas.Build(tool.OutputType!, tool.JsonOptions) is not null, tool.Name + " answer");
        }
    }

    /// <summary>A call of a role with only <paramref name="permissions"/>.</summary>
    private sealed class RoleCall(ICallContext inner, HashSet<string> permissions) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public IDeviceRepository Devices => inner.Devices;

        public IVapixClientFactory Vapix => inner.Vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

        public IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => permissions.Contains(permission);

        public bool Contains(IDeviceInfo device) => inner.Contains(device);
    }
}
