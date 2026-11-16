using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;

using Oadm.Plugins.VpnFleet.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.VpnFleet.Tests;

/// <summary>
/// The table between server and page: events carry only the changed devices, GetState comes in pages, the page applies
/// changes in place with versions and keeps the selection.
/// </summary>
public sealed class TableEventTests
{
    [Fact]
    public async Task A_change_of_one_camera_sends_only_its_rows()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var cameras = Enumerable.Range(0, 3).Select(i =>
        {
            var camera = new AppCamera().WithApp("Tailscale_VPN");
            camera.Parameters["Tailscale_VPN.AcceptDNS"] = "false";
            return (Device: server.Add(new FakeDevice { Address = $"10.0.0.{48 + i}" }, camera), Camera: camera);
        }).ToList();
        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(true, []), Fleet.Json));
        await FleetEvents.NextStateAsync(server, s => !s.Reading && s.Rows.Count == 3);
        server.PublishedEvents.Clear();

        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.SaveSettings,
            JsonSerializer.Serialize(new SaveSettingsRequest(cameras[1].Device.Id, VpnKind.Tailscale, new Dictionary<string, string> { ["AcceptDNS"] = "true" }), Fleet.Json));

        var sent = Assert.Single(server.PublishedEvents);
        Assert.Equal(Fleet.RowsTopic, sent.Topic);
        var change = JsonSerializer.Deserialize<FleetRowsEvent>(sent.PayloadJson!, Fleet.Json)!;
        Assert.Equal([cameras[1].Device.Id], change.Devices);
        Assert.Equal(cameras[1].Device.Id, Assert.Single(change.Rows).DeviceId);
    }

    [Fact]
    public async Task The_table_comes_in_pages_in_id_order()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        for (var i = 0; i < 5; i++)
        {
            server.Add(new FakeDevice { Address = $"10.0.0.{48 + i}" }, new AppCamera().WithApp("Tailscale_VPN"));
        }

        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(true, []), Fleet.Json));
        await FleetEvents.NextStateAsync(server, s => !s.Reading && s.Rows.Count == 5);

        var pages = new List<FleetState>();
        Guid? after = null;
        do
        {
            var json = await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.GetState, JsonSerializer.Serialize(new StatePageRequest(after, 2), Fleet.Json));
            var page = JsonSerializer.Deserialize<FleetState>(json!, Fleet.Json)!;
            pages.Add(page);
            after = page.NextAfter;
        }
        while (after is not null);

        Assert.Equal([2, 2, 1], pages.Select(p => p.Rows.Count));
        var ids = pages.SelectMany(p => p.Rows).Select(r => r.DeviceId).ToList();
        Assert.Equal(ids.Order(), ids);
        Assert.Equal(server.DeviceList.Select(d => d.Id).Order(), ids);
    }

    [Fact]
    public async Task The_page_reads_the_pages_then_follows_the_events_of_one_device()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var a = server.Add(new FakeDevice { Address = "10.0.0.48" }, new AppCamera().WithApp("Tailscale_VPN"));
        server.Add(new FakeDevice { Address = "10.0.0.49" }, new AppCamera());
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [.. server.DeviceList] };
        using var page = new VpnFleetViewModel(ctx);
        await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.Refresh, JsonSerializer.Serialize(new RefreshRequest(true, []), Fleet.Json));
        await FleetEvents.NextStateAsync(server, s => !s.Reading && s.Rows.Count == 2);

        await page.ReadStateAsync(CancellationToken.None);
        Assert.Equal(["10.0.0.48", "10.0.0.49"], page.Rows.Select(r => r.Address));
        var row = page.Rows[0];

        var later = new FleetRow(a.Id, "10.0.0.48", "", VpnKind.Tailscale, "1.104.7", VpnState.Connected, "100.64.0.9", null, null);
        page.HandleEvent(new PluginEvent(Fleet.RowsTopic, JsonSerializer.Serialize(new FleetRowsEvent(int.MaxValue - 1, [a.Id], [later]), Fleet.Json)));
        await Eventually.TrueAsync(() => !page.IsApplying, "the event is applied");

        Assert.Same(row, page.Rows[0]);
        Assert.Equal("100.64.0.9", row.VpnAddress);
    }

    [Fact]
    public void One_row_changes_in_place_without_a_reset_and_the_selection_stays()
    {
        var (page, ids) = PageWith(3);
        page.SelectedRow = page.Rows[1];
        var camera = page.Camera;
        var resets = CountResets(page);

        page.ApplyBatch([new FleetRowsEvent(10, [ids[1]], [Row(ids[1], "10.0.0.2", VpnState.NotConnected)])], []);

        Assert.Equal(0, resets());
        Assert.Same(page.Rows[1], page.SelectedRow);
        Assert.Same(camera, page.Camera);
        Assert.Equal("Not connected", page.Rows[1].StateText);
        Assert.Equal("3 devices: 2 connected, 1 not connected", page.Summary);
    }

    [Fact]
    public void Rows_that_come_or_go_reset_the_grid_once_and_the_selection_stays()
    {
        var (page, ids) = PageWith(3);
        var selected = page.SelectedRow = page.Rows[2];
        var resets = CountResets(page);
        var added = Guid.NewGuid();

        page.ApplyBatch(
            [
                new FleetRowsEvent(10, [ids[0]], []),
                new FleetRowsEvent(11, [added], [Row(added, "10.0.0.9", VpnState.Connected)]),
            ],
            []);

        Assert.Equal(1, resets());
        Assert.Equal(["10.0.0.2", "10.0.0.3", "10.0.0.9"], page.Rows.Select(r => r.Address));
        Assert.Same(selected, page.SelectedRow);
    }

    [Fact]
    public void Older_rows_never_replace_newer_ones_and_an_emptied_table_drops_older_events()
    {
        var (page, ids) = PageWith(2); // snapshot version 5

        page.ApplyBatch([new FleetRowsEvent(4, [ids[0]], [Row(ids[0], "10.0.0.1", VpnState.NotConnected)])], []);
        Assert.Equal("Connected", page.Rows[0].StateText);

        page.ApplyBatch([new FleetRowsEvent(7, [ids[0]], [Row(ids[0], "10.0.0.1", VpnState.NotConnected)])], []);
        page.ApplyBatch([new FleetRowsEvent(6, [ids[0]], [Row(ids[0], "10.0.0.1", VpnState.Connected)])], []);
        Assert.Equal("Not connected", page.Rows[0].StateText);

        page.ApplyBatch([new FleetRowsEvent(8, [ids[1]], [Row(ids[1], "10.0.0.2", VpnState.Connected)])], [new FleetState(9, false, []) { Cleared = true }]);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task A_burst_of_events_is_applied_together()
    {
        var (page, ids) = PageWith(3);
        var resets = CountResets(page);
        var added = Guid.NewGuid();

        page.HandleEvent(new PluginEvent(Fleet.StateTopic, JsonSerializer.Serialize(new FleetState(10, true, []), Fleet.Json)));
        page.HandleEvent(new PluginEvent(Fleet.RowsTopic, JsonSerializer.Serialize(new FleetRowsEvent(11, [added], [Row(added, "10.0.0.9", VpnState.Connected)]), Fleet.Json)));
        page.HandleEvent(new PluginEvent(Fleet.RowsTopic, JsonSerializer.Serialize(new FleetRowsEvent(12, [ids[0]], [Row(ids[0], "10.0.0.1", VpnState.AppStopped)]), Fleet.Json)));
        await Eventually.TrueAsync(() => !page.IsApplying, "the events are applied");

        Assert.True(resets() <= 2); // one per batch; the burst may be split once
        Assert.Equal(4, page.Rows.Count);
        Assert.True(page.IsReading);
        Assert.Equal("App stopped", page.Rows[0].StateText);
    }

    [Fact]
    public void The_search_shows_matching_rows_and_follows_their_changes()
    {
        var (page, ids) = PageWith(3);

        page.SearchText = "not connected";
        Assert.Empty(page.Rows);

        page.ApplyBatch([new FleetRowsEvent(10, [ids[2]], [Row(ids[2], "10.0.0.3", VpnState.NotConnected)])], []);
        Assert.Equal(["10.0.0.3"], page.Rows.Select(r => r.Address));

        page.SearchText = "";
        Assert.Equal(3, page.Rows.Count);
    }

    [Fact]
    public void Addresses_sort_by_number()
    {
        var (page, _) = PageWith(0);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        page.ApplyBatch([new FleetRowsEvent(1, [a, b], [Row(a, "10.0.0.10", VpnState.Connected), Row(b, "10.0.0.9", VpnState.Connected)])], []);

        Assert.Equal(["10.0.0.9", "10.0.0.10"], page.Rows.Select(r => r.Address));
    }

    [Fact]
    public void Only_GitHub_pages_open()
    {
        var (page, _) = PageWith(0);
        var opened = new List<Uri>();
        page.Launch = uri =>
        {
            opened.Add(uri);
            return Task.CompletedTask;
        };

        page.OpenUrl("https://example.com/releases");
        page.OpenUrl("http://github.com/Mo3he");
        page.OpenUrl("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/latest");

        Assert.Equal("https://github.com/Mo3he/Axis_Cam_Tailscale/releases/latest", Assert.Single(opened).ToString());
    }

    /// <summary>A page whose table has <paramref name="count"/> connected Tailscale cameras 10.0.0.1, 10.0.0.2, ... at version 5.</summary>
    internal static (VpnFleetViewModel Page, IReadOnlyList<Guid> Ids) PageWith(int count)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();
        var page = new VpnFleetViewModel(new NoServerContext());
        page.Apply(JsonSerializer.Serialize(new FleetState(5, false, [.. ids.Select((id, i) => Row(id, $"10.0.0.{i + 1}", VpnState.Connected))]), Fleet.Json));
        return (page, ids);
    }

    internal static FleetRow Row(Guid id, string address, VpnState state) =>
        new(id, address, "P3265-V", VpnKind.Tailscale, "1.104.7", state, null, null, null);

    /// <summary>Counts the resets of the shown rows from now on.</summary>
    internal static Func<int> CountResets(VpnFleetViewModel page)
    {
        var resets = 0;
        page.Rows.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;
        return () => resets;
    }

    /// <summary>A page context without a server, for tests that feed the page directly.</summary>
    private sealed class NoServerContext : Oadm.Sdk.Client.ICorePluginClientContext
    {
        public IReadOnlyList<IDeviceInfo> Devices => [];

        public IReadOnlyList<IDeviceInfo> SelectedDevices => [];

        public bool Has(string permission) => true;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);

        public async IAsyncEnumerable<PluginEvent> WatchEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
    }
}

/// <summary>New key pair: made on the server, the private key goes only to its camera, the page sees the public key.</summary>
public sealed class NewKeyPairTests
{
    [Fact]
    public async Task The_server_makes_the_key_pair_and_writes_the_private_key_to_that_camera_only()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera().WithApp("wireguardconfig");
        var old = WireGuardKeys.Generate();
        camera.Parameters["wireguardconfig.PrivateKey"] = old.PrivateKey;
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);
        var other = new AppCamera().WithApp("wireguardconfig");
        server.Add(new FakeDevice { Address = "10.0.0.49" }, other);

        var json = await server.InvokeAsync(plugin, server.CallAsAllDevices(), Fleet.NewKeyPair, JsonSerializer.Serialize(new NewKeyPairRequest(device.Id), Fleet.Json));

        var written = camera.Parameters["root.wireguardconfig.PrivateKey"];
        Assert.True(WireGuardKeys.IsKey(written));
        Assert.NotEqual(old.PrivateKey, written);
        var reply = JsonSerializer.Deserialize<NewKeyPairReply>(json!, Fleet.Json)!;
        Assert.Equal(WireGuardKeys.PublicKeyOf(written), reply.PublicKey);
        Assert.DoesNotContain(written, json, StringComparison.Ordinal);
        Assert.Equal(0, other.Writes);
    }

    [Fact]
    public async Task A_new_key_pair_needs_the_right_to_change_settings_and_is_audited_without_a_key()
    {
        var (plugin, _, _, _) = await TestPlugin.StartAsync();

        Assert.Equal(Fleet.ConnectPermission, plugin.RequiredPermission(Fleet.NewKeyPair));
        Assert.True(plugin.IsAudited(Fleet.NewKeyPair));
        Assert.Equal("New WireGuard key pair", plugin.AuditDetail(Fleet.NewKeyPair, """{"deviceId":"00000000-0000-0000-0000-000000000001"}"""));
    }

    [Fact]
    public async Task The_panel_asks_then_shows_the_new_public_key_and_never_holds_the_private_key()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var camera = new AppCamera().WithApp("wireguardconfig");
        camera.Parameters["wireguardconfig.PrivateKey"] = WireGuardKeys.Generate().PrivateKey;
        camera.Parameters["wireguardconfig.ClientIP"] = "10.66.0.2/24";
        var device = server.Add(new FakeDevice { Address = "10.0.0.48" }, camera);
        var ctx = new FakePageContext(plugin, server) { SelectedDevices = [device] };
        var row = new FleetRowView(new FleetRow(device.Id, "10.0.0.48", "", VpnKind.WireGuard, "1.0.0", VpnState.Connected, "10.66.0.2", null, null));
        var panel = new CameraSettingsViewModel(ctx, row, mayChange: true, mayInstall: true, CancellationToken.None);
        await panel.LoadAsync();

        ctx.Confirms = false;
        await panel.NewKeyPairCommand.ExecuteAsync(null);
        Assert.Equal(0, camera.Writes);

        ctx.Confirms = true;
        await panel.NewKeyPairCommand.ExecuteAsync(null);

        var written = camera.Parameters["root.wireguardconfig.PrivateKey"];
        Assert.Equal(WireGuardKeys.PublicKeyOf(written), panel.PublicKey);
        Assert.DoesNotContain(panel.Fields, f => f.Value.Contains(written, StringComparison.Ordinal));
        Assert.Equal(2, ctx.Asked.Count);
        Assert.StartsWith("The camera has a new key pair.", panel.InfoText, StringComparison.Ordinal);
    }
}

/// <summary>Profiles: the size decides in the view model, the error shows below the profile field.</summary>
public sealed class ProfileTests
{
    [Fact]
    public async Task A_profile_too_large_is_refused_below_its_field_in_the_set_up_form()
    {
        var form = new ConnectFormViewModel { SelectedVpn = new VpnChoice(VpnKind.OpenVpn, "OpenVPN") };
        form.PickProfile = () => Task.FromResult<ProfileFile?>(new ProfileFile("big.ovpn", ConnectRules.MaxProfileLength + 1, null));

        await form.ChooseProfileCommand.ExecuteAsync(null);

        Assert.Equal(ConnectRules.ProfileTooLarge, form.ProfileError);
        Assert.Equal("big.ovpn", form.ProfileName);
        Assert.False(form.IsFormValid);
    }

    [Fact]
    public async Task The_camera_panel_shows_a_profile_problem_below_the_profile_field()
    {
        var (plugin, server, _, _) = await TestPlugin.StartAsync();
        var ctx = new FakePageContext(plugin, server);
        var row = new FleetRowView(new FleetRow(Guid.NewGuid(), "10.0.0.48", "", VpnKind.OpenVpn, "1.0.0", VpnState.Connected, null, null, null));
        var panel = new CameraSettingsViewModel(ctx, row, mayChange: true, mayInstall: true, CancellationToken.None)
        {
            PickProfile = () => Task.FromResult<ProfileFile?>(new ProfileFile("big.ovpn", ConnectRules.MaxProfileLength + 1, null)),
        };

        await panel.ChooseProfileCommand.ExecuteAsync(null);

        Assert.Equal(ConnectRules.ProfileTooLarge, panel.ProfileError);
        Assert.Null(panel.ErrorText);
        Assert.Equal(ConnectRules.ProfileTooLarge, panel.SaveBlockedReason);

        panel.PickProfile = () => Task.FromResult<ProfileFile?>(new ProfileFile("site.ovpn", 30, "client\nremote vpn.example.com 1194\n"));
        await panel.ChooseProfileCommand.ExecuteAsync(null);
        Assert.Null(panel.ProfileError);
        Assert.Null(panel.SaveBlockedReason);
    }
}

/// <summary>The cameras of a wizard at the device limit: ticks are counted as they change, the search is one reset.</summary>
public sealed class ChecklistScaleTests
{
    [Fact]
    public void A_hundred_thousand_cameras_tick_search_and_select_all() => TheMostCameras();

    [Fact]
    [Trait("Category", "Perf")]
    public void A_hundred_thousand_cameras_tick_search_and_select_all_fast()
    {
        var elapsed = TheMostCameras();
        Assert.True(elapsed < 10_000, $"set, ticks, search and select all took {elapsed} ms");
    }

    private static long TheMostCameras()
    {
        const int Cameras = DeviceLimits.MaxDevices;
        var items = Enumerable.Range(0, Cameras)
            .Select(i => new CameraCheck(Guid.NewGuid(), $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", "P3265-V", "1.104.6", "Upgrade", i % 2 == 0 ? CheckKind.Upgrade : CheckKind.Install, i % 2 == 0))
            .ToList();
        var list = new CameraChecklist();
        var changes = 0;
        list.Changed += (_, _) => changes++;

        var watch = Stopwatch.StartNew();
        list.Set(items);
        Assert.Equal(Cameras / 2, list.CheckedCount);

        for (var i = 1; i < 2_001; i += 2)
        {
            items[i].IsChecked = true; // a tick: +1, never a count over all cameras
        }

        Assert.Equal((Cameras / 2) + 1_000, list.CheckedCount);

        list.Search = "10.1.";
        Assert.Equal(items.Count(c => c.Matches("10.1.")), list.Items.Count);
        list.CheckNoneCommand.Execute(null);
        list.Search = "";
        list.CheckAllCommand.Execute(null);
        Assert.Equal(Cameras, list.CheckedCount);
        list.CheckOnly(CheckKind.Install);
        Assert.Equal(Cameras / 2, list.CheckedCount);
        Assert.Equal(Cameras / 2, list.CheckedIds.Count);
        var elapsed = watch.ElapsedMilliseconds;

        Assert.Equal($"{Cameras / 2:N0} of {Cameras:N0} cameras ticked.", list.Summary);
        Assert.True(changes > 1_000);
        Console.WriteLine($"{Cameras:N0} cameras: set, 1,000 ticks, search and select all {elapsed} ms");
        return elapsed;
    }
}
