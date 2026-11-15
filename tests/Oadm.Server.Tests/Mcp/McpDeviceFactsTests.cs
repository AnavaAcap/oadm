using System.Text.Json.Nodes;

using Oadm.Contracts.Vulnerabilities;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Vapix;
using Oadm.Server.Assistant;
using Oadm.Server.Tests.Assistant;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;
using Oadm.Server.Vulnerabilities;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// What OADM knows of the devices without asking them (docs/spec/mcp.md "Devices and the device filter", "Other areas"): the
/// Axis CVEs as the client's Vulnerabilities column matches them, the possible ones apart and never counted; the clock, the
/// certificate and the chip as filters, row fields and counts; the caller's device group; the Assistant's table showing the
/// fact the question filtered by.
/// </summary>
public sealed class McpDeviceFactsTests
{
    /// <summary>A non-LTS AXIS OS with counted CVEs in the base list, and an LTS build the records give no fix for (possible only).</summary>
    private const string Affected = "11.9.65";
    private const string LtsPossible = "10.12.363";
    private const string Newest = "99.1.1";

    private static async Task<Device> AddAsync(TestServerHost host, int n, string model, string firmware, Action<Device>? set = null, params string[] tags)
    {
        var device = new Device
        {
            Serial = FakeSerials.Make(8800 + n), Address = "10.88.0." + n, Scheme = DeviceScheme.Http, Status = Oadm.Sdk.Devices.DeviceStatus.Ok,
            Model = model, FirmwareVersion = firmware,
        };
        set?.Invoke(device);
        await host.Get<DeviceRepository>().AddAsync(device, CancellationToken.None);
        if (tags.Length > 0)
        {
            await host.Tags.SetDeviceTagsAsync(new Proto.SetDeviceTagsRequest { DeviceIds = { device.Id.ToString() }, Add = { tags } });
        }

        return device;
    }

    /// <summary>The client's column for a device: the same matcher, the model, the AXIS OS and the chip as the SoC column shows it ("A8").</summary>
    private static DeviceVulnerabilities ColumnOf(VulnerabilityMatcher matcher, string model, string firmware, string? shortSoc)
    {
        var items = matcher.Match(model, firmware, shortSoc);
        return items.Count == 0 ? DeviceVulnerabilities.None : new DeviceVulnerabilities(items);
    }

    private static string ColumnText(DeviceVulnerabilities found) => string.Join(' ', new[] { found.Text, found.PossibleText }.Where(t => t.Length > 0));

    private static IEnumerable<string> Ids(JsonNode? devices) => devices!.AsArray().Select(d => d!["id"]!.GetValue<string>());

    [Fact]
    public async Task TheVulnerabilityToolsCountAffectedDevicesKeepPossibleOnesApartAndSayWhatTheColumnSays()
    {
        await using var server = await McpTestServer.StartAsync();
        var matcher = server.Host.Get<VulnerabilityStore>().Matcher;
        var affected = await AddAsync(server.Host, 1, "M3085-V", Affected);
        var possible = await AddAsync(server.Host, 2, "M3206-LVE", LtsPossible);
        var clean = await AddAsync(server.Host, 3, "M3085-V", Newest);
        var lobby = await AddAsync(server.Host, 4, "AXIS Q3538-LVE", Affected, d => d.Soc = "Axis Artpec-8", "Lobby");

        // The base list as the client column sees these devices (the chip short, as the SoC column has it).
        var columns = new Dictionary<string, DeviceVulnerabilities>
        {
            [affected.Id.ToString()] = ColumnOf(matcher, "M3085-V", Affected, null),
            [possible.Id.ToString()] = ColumnOf(matcher, "M3206-LVE", LtsPossible, null),
            [clean.Id.ToString()] = ColumnOf(matcher, "M3085-V", Newest, null),
            [lobby.Id.ToString()] = ColumnOf(matcher, "AXIS Q3538-LVE", Affected, "A8"),
        };
        Assert.True(columns[affected.Id.ToString()].Certain > 0, "the base list has counted CVEs for " + Affected);
        Assert.True(columns[lobby.Id.ToString()].Certain > 0, "the base list has counted CVEs for an A8 on " + Affected);
        Assert.Equal(0, columns[possible.Id.ToString()].Certain);
        Assert.True(columns[possible.Id.ToString()].PossibleCount > 0, "the base list has possible CVEs for " + LtsPossible);
        Assert.False(columns[clean.Id.ToString()].Any);
        var key = await server.CreateKeyAsync();

        var summary = await server.ToolAsync(key, "oadm_vulnerabilities_summary");
        Assert.Equal(4, summary["devices"]!.GetValue<int>());
        Assert.Equal(2, summary["affected"]!.GetValue<int>());
        Assert.Equal(1, summary["possible_only"]!.GetValue<int>()); // apart, never in affected
        Assert.Equal(1, summary["not_affected"]!.GetValue<int>());
        Assert.Equal(2, summary["by_severity"]!.AsArray().Sum(l => l!["devices"]!.GetValue<int>()));
        Assert.Equal(server.Host.Get<VulnerabilityStore>().Current.Items.Count, summary["list"]!["cves"]!.GetValue<int>());
        Assert.Equal("release", summary["list"]!["source"]!.GetValue<string>());
        var top = summary["top_cves"]!.AsArray();
        Assert.InRange(top.Count, 1, VulnerabilityTools.TopCves);
        Assert.All(top, c => Assert.InRange(c!["devices"]!.GetValue<int>(), 1, 2)); // counted only: the LTS device's possible CVEs are not in it
        var counts = top.Select(c => c!["devices"]!.GetValue<int>()).ToList();
        Assert.Equal(counts.OrderByDescending(c => c), counts);

        // The devices with their CVEs, exactly as the column says them.
        var list = await server.ToolAsync(key, "oadm_vulnerabilities");
        Assert.Equal(3, list["total"]!.GetValue<int>());
        foreach (var row in list["devices"]!.AsArray().Select(r => r!.AsObject()))
        {
            var column = columns[row["id"]!.GetValue<string>()];
            Assert.Equal(ColumnText(column), row["vulnerabilities"]!.GetValue<string>());
            Assert.Equal(column.Items.Where(i => !i.Possible).Select(i => i.Vulnerability.Id), row["cves"]!.AsArray().Select(c => c!["cve"]!.GetValue<string>()));
            Assert.Equal(column.Items.Where(i => i.Possible).Select(i => i.Vulnerability.Id), row["possible"]!.AsArray().Select(c => c!["cve"]!.GetValue<string>()));
            Assert.Equal(column.Items.Where(i => !i.Possible).Select(i => i.FixedIn), row["cves"]!.AsArray().Select(c => c!["fixed_in"]?.GetValue<string>()));
        }

        var possibleRow = list["devices"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == possible.Id.ToString())!;
        Assert.Empty(possibleRow["cves"]!.AsArray());
        Assert.EndsWith("possible", possibleRow["vulnerabilities"]!.GetValue<string>(), StringComparison.Ordinal);

        // Filters: counted only, only possible, a level, one CVE; the device search takes the same filter and shows the column.
        var counted = await server.ToolAsync(key, "oadm_vulnerabilities", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["state"] = "affected" } } });
        Assert.Equal(new[] { affected.Id.ToString(), lobby.Id.ToString() }.Order(), Ids(counted["devices"]).Order());
        var onlyPossible = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["state"] = "possible" } } });
        Assert.Equal([possible.Id.ToString()], Ids(onlyPossible["devices"]));
        Assert.Equal(ColumnText(columns[possible.Id.ToString()]), onlyPossible["devices"]![0]!["vulnerabilities"]!.GetValue<string>());
        var none = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["state"] = "none" } } });
        Assert.Equal([clean.Id.ToString()], Ids(none["devices"]));
        var worst = columns[affected.Id.ToString()].Items[0].Vulnerability.Severity;
        var severe = await server.ToolAsync(key, "oadm_vulnerabilities", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["min_severity"] = worst } } });
        Assert.Contains(affected.Id.ToString(), Ids(severe["devices"]));
        Assert.DoesNotContain(possible.Id.ToString(), Ids(severe["devices"])); // a possible CVE sets no level
        var possibleCve = columns[possible.Id.ToString()].Items[0].Vulnerability.Id;
        var byCve = await server.ToolAsync(key, "oadm_vulnerabilities", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["cve"] = possibleCve.ToLowerInvariant() } } });
        Assert.Contains(possible.Id.ToString(), Ids(byCve["devices"]));

        // Pages with the total and a cursor.
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = await server.ToolAsync(key, "oadm_vulnerabilities", cursor is null ? new JsonObject { ["limit"] = 1 } : new JsonObject { ["limit"] = 1, ["cursor"] = cursor });
            Assert.Equal(3, page["total"]!.GetValue<int>());
            seen.AddRange(Ids(page["devices"]));
            cursor = page["next_cursor"]?.GetValue<string>();
        }
        while (cursor is not null);
        Assert.Equal(3, seen.Distinct().Count());

        // A user of a device group sees and counts only the group's devices.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker);
        var mine = await server.ToolAsync(groupKey, "oadm_vulnerabilities_summary");
        Assert.Equal(1, mine["devices"]!.GetValue<int>());
        Assert.Equal(1, mine["affected"]!.GetValue<int>());
        Assert.Equal(0, mine["possible_only"]!.GetValue<int>());
        Assert.All(mine["top_cves"]!.AsArray(), c => Assert.Equal(1, c!["devices"]!.GetValue<int>()));
        var mineList = await server.ToolAsync(groupKey, "oadm_vulnerabilities");
        Assert.Equal([lobby.Id.ToString()], Ids(mineList["devices"]));
        var foreign = await server.CallToolAsync(groupKey, "oadm_vulnerabilities", new JsonObject { ["filter"] = new JsonObject { ["ids"] = new JsonArray(possible.Id.ToString()) } });
        Assert.True(foreign["isError"]!.GetValue<bool>()); // a device outside the group answers like an unknown one
    }

    [Fact]
    public async Task TheDeviceToolsFilterShowAndCountTheClockTheCertificateAndTheChipOadmAlreadyKnows()
    {
        await using var server = await McpTestServer.StartAsync();
        var now = DateTime.UtcNow;
        var drift = await AddAsync(server.Host, 11, "M3085-V", Newest, d =>
        {
            d.ClockOffsetMs = 50_000;
            d.NtpState = NtpState.Synced;
            d.CertNotAfterUtc = now.AddDays(10);
            d.CertTrust = CertificateTrust.SelfSigned;
            d.Soc = "Axis Artpec-8";
        }, "Lobby");
        var notSynced = await AddAsync(server.Host, 12, "M3085-V", Newest, d =>
        {
            d.ClockOffsetMs = 0;
            d.NtpState = NtpState.NotSynced;
            d.CertNotAfterUtc = now.AddDays(-2);
            d.CertTrust = CertificateTrust.Expired;
        });
        var fine = await AddAsync(server.Host, 13, "M3085-V", Newest, d =>
        {
            d.ClockOffsetMs = 100;
            d.NtpState = NtpState.Synced;
            d.CertNotAfterUtc = now.AddDays(400);
            d.CertTrust = CertificateTrust.Trusted;
        });
        await AddAsync(server.Host, 14, "M3085-V", Newest);
        var key = await server.CreateKeyAsync();

        var summary = await server.ToolAsync(key, "oadm_devices_summary", new JsonObject { ["group_by"] = "clock" });
        var look = summary["needs_a_look"]!;
        Assert.Equal((2, 1), (look["clock_off"]!.GetValue<int>(), look["clock_not_measured"]!.GetValue<int>()));
        Assert.Equal((1, 1, 2), (look["certificates_expired"]!.GetValue<int>(), look["certificates_expiring_soon"]!.GetValue<int>(), look["certificates_not_trusted"]!.GetValue<int>()));
        Assert.Equal(0, look["vulnerable"]!.GetValue<int>());
        var groups = summary["groups"]!.AsArray().ToDictionary(g => g!["value"]!.GetValue<string>(), g => g!["count"]!.GetValue<int>());
        Assert.Equal(new Dictionary<string, int> { ["Off"] = 2, ["OK"] = 1, ["Not measured"] = 1 }, groups);

        // "Which devices are not NTP synced or have a drift": the clock filter, and the rows show the clock by themselves.
        var off = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["clock"] = new JsonArray("off") } });
        Assert.Equal(2, off["total"]!.GetValue<int>());
        var rows = off["devices"]!.AsArray().ToDictionary(r => r!["id"]!.GetValue<string>(), r => r!.AsObject());
        Assert.Equal("50 s ahead", rows[drift.Id.ToString()]["clock"]!.GetValue<string>());
        Assert.Equal("NTP not synced", rows[notSynced.Id.ToString()]["clock"]!.GetValue<string>());
        Assert.True(rows[drift.Id.ToString()]["clock_off"]!.GetValue<bool>());
        Assert.Equal("NotSynced", rows[notSynced.Id.ToString()]["ntp"]!.GetValue<string>());
        Assert.Equal(50_000, rows[drift.Id.ToString()]["clock_offset_ms"]!.GetValue<int>());

        var far = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["clock_offset_over_seconds"] = 10 } });
        Assert.Equal([drift.Id.ToString()], Ids(far["devices"]));
        var unsynced = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["ntp"] = new JsonArray("NotSynced") } });
        Assert.Equal([notSynced.Id.ToString()], Ids(unsynced["devices"]));

        var ending = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["certificate"] = new JsonObject { ["expires_within_days"] = 30 } } });
        var endingRows = ending["devices"]!.AsArray().ToDictionary(r => r!["id"]!.GetValue<string>(), r => r!.AsObject());
        Assert.Equal(new[] { drift.Id.ToString(), notSynced.Id.ToString() }.Order(), endingRows.Keys.Order());
        Assert.Equal("Expires soon", endingRows[drift.Id.ToString()]["certificate_state"]!.GetValue<string>());
        Assert.StartsWith("Expired", endingRows[notSynced.Id.ToString()]["certificate_expires_in"]!.GetValue<string>(), StringComparison.Ordinal);
        var trusted = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["certificate"] = new JsonObject { ["trust"] = new JsonArray("Trusted") } } });
        Assert.Equal([fine.Id.ToString()], Ids(trusted["devices"]));

        var chip = await server.ToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["socs"] = new JsonArray("A8") } });
        Assert.Equal([drift.Id.ToString()], Ids(chip["devices"]));
        Assert.Equal("Axis Artpec-8", chip["devices"]![0]!["soc"]!.GetValue<string>());

        // Without such a filter the rows stay short.
        var plain = await server.ToolAsync(key, "oadm_devices_search");
        Assert.Null(plain["devices"]![0]!["clock"]);

        // A user of a device group counts the group's devices only.
        var group = await GroupCallers.ForTagAsync(server.Host, "tech", "Lobby");
        var groupKey = await server.CreateKeyAsync(group.Invoker);
        var mine = await server.ToolAsync(groupKey, "oadm_devices_summary");
        Assert.Equal(1, mine["total"]!.GetValue<int>());
        Assert.Equal(1, mine["needs_a_look"]!["clock_off"]!.GetValue<int>());
        Assert.Equal(0, mine["needs_a_look"]!["certificates_expired"]!.GetValue<int>());
        var mineOff = await server.ToolAsync(groupKey, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["clock"] = new JsonArray("off") } });
        Assert.Equal([drift.Id.ToString()], Ids(mineOff["devices"]));
    }

    [Fact]
    public async Task AFactFilterIsRefusedWithItsFieldErrorAndKeepsARemovalFromTakingEveryDevice()
    {
        await using var server = await McpTestServer.StartAsync();
        await AddAsync(server.Host, 21, "M3085-V", Newest);
        var key = await server.CreateKeyAsync();

        var bad = await server.CallToolAsync(key, "oadm_devices_search", new JsonObject { ["filter"] = new JsonObject { ["clock"] = new JsonArray("late") } });
        Assert.True(bad["isError"]!.GetValue<bool>());
        var severity = await server.CallToolAsync(key, "oadm_vulnerabilities", new JsonObject { ["filter"] = new JsonObject { ["vulnerabilities"] = new JsonObject { ["min_severity"] = "Severe" } } });
        Assert.True(severity["isError"]!.GetValue<bool>());

        // A filter of facts only is a filter: the removal preview takes only the devices it names.
        var preview = await server.ToolAsync(key, "oadm_devices_remove_preview", new JsonObject { ["filter"] = new JsonObject { ["clock"] = new JsonArray("off") } });
        Assert.Equal(0, preview["devices"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheAssistantsTableShowsTheFactTheQuestionFilteredByInsteadOfTheStatus()
    {
        await using var server = await AssistantTestServer.StartAsync();
        await AddAsync(server.Host, 31, "M3085-V", Newest, d =>
        {
            d.ClockOffsetMs = 50_000;
            d.NtpState = NtpState.Synced;
        });
        await AddAsync(server.Host, 32, "M3206-LVE", LtsPossible);

        server.Model
            .Call(("oadm_devices_search", AssistantTestServer.Args("""{"filter":{"clock":["off"]}}""")), ("oadm_vulnerabilities", null), ("oadm_devices_search", null))
            .Say("One clock is off.");
        var events = await server.ChatAsync("Which devices are not NTP synced or have a drift?");
        var tables = events.Where(e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Table).Select(e => e.Table).ToList();
        Assert.Equal(3, tables.Count);
        Assert.Equal("Clock", tables[0].DetailHeader);
        Assert.Equal("50 s ahead", tables[0].Rows.Single().Detail);
        Assert.Single(tables[0].AllDeviceIds);
        Assert.Equal("Vulnerabilities", tables[1].DetailHeader);
        Assert.EndsWith("possible", tables[1].Rows.Single().Detail, StringComparison.Ordinal);
        Assert.Single(tables[1].AllDeviceIds); // the tool's own default: only devices with a CVE
        Assert.Equal(string.Empty, tables[2].DetailHeader); // no fact asked: the status
        Assert.Equal(2, tables[2].Rows.Count);
        Assert.Contains(events, e => e.KindCase == Proto.AssistantEvent.KindOneofCase.Tool && e.Tool.Text == "Checking vulnerabilities");
    }

    [Fact]
    public void TheTablesDetailFollowsTheFilter()
    {
        Assert.Equal(("Clock", "clock"), AssistantCards.DetailOf("oadm_devices_search", new Proto.DeviceFilter { Clock = { "off" } }));
        Assert.Equal(("Certificate expires", "certificate_expires_in"), AssistantCards.DetailOf("oadm_devices_search", new Proto.DeviceFilter { CertificateExpiresWithinDays = 30 }));
        Assert.Equal(("Vulnerabilities", "vulnerabilities"), AssistantCards.DetailOf("oadm_devices_search", new Proto.DeviceFilter { Cve = "CVE-2024-6509" }));
        Assert.Equal(("SoC", "soc"), AssistantCards.DetailOf("oadm_devices_search", new Proto.DeviceFilter { Socs = { "A8" } }));
        Assert.Equal(("Vulnerabilities", "vulnerabilities"), AssistantCards.DetailOf(AssistantCards.VulnerabilitiesTool, new Proto.DeviceFilter()));
        Assert.Null(AssistantCards.DetailOf("oadm_devices_search", new Proto.DeviceFilter { Models = { "M3085-V" } }));
    }
}
