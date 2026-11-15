using Grpc.Core;

using Oadm.Core.Devices;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// <c>DeviceService.Search</c> (docs/spec/grpc.md): the caller's devices filtered on the server, one page after the last row's
/// sort key and id, the total, the matches after the cursor and the counts of a field; only the page is mapped.
/// </summary>
public sealed class DeviceSearchTests
{
    [Fact]
    public async Task TheFilterCombinesItsFieldsWithAndAndAListMatchesAnyOfItsValues()
    {
        await using var host = await TestServerHost.StartAsync();
        var repository = host.Get<DeviceRepository>();
        await repository.AddAsync(new Device { Serial = FakeSerials.Make(4001), Address = "10.2.0.1", HostName = "lobby-1", Model = "M3085-V", FirmwareVersion = "11.11.9", Status = Oadm.Sdk.Devices.DeviceStatus.Ok, Tags = ["Lobby"], Category = Oadm.Sdk.Devices.DeviceCategory.Camera }, CancellationToken.None);
        await repository.AddAsync(new Device { Serial = FakeSerials.Make(4002), Address = "10.2.0.2", UpnpFriendlyName = "Yard speaker", Model = "C1310-E", FirmwareVersion = "12.11.77", Status = Oadm.Sdk.Devices.DeviceStatus.Unreachable, Category = Oadm.Sdk.Devices.DeviceCategory.Speaker }, CancellationToken.None);
        await repository.AddAsync(new Device { Serial = FakeSerials.Make(4003), Address = "10.2.0.10", Model = "m3085-v", FirmwareVersion = "12.9.1", Status = Oadm.Sdk.Devices.DeviceStatus.Ok, Tags = ["Lobby", "PTZ"], Category = Oadm.Sdk.Devices.DeviceCategory.Camera }, CancellationToken.None);
        var devices = new Proto.DeviceService.DeviceServiceClient(host.Invoker);

        async Task<IReadOnlyList<string>> AddressesAsync(Proto.DeviceFilter filter) =>
            [.. (await devices.SearchAsync(new Proto.SearchDevicesRequest { Filter = filter, Limit = 10 })).Devices.Select(d => d.Address)];

        Assert.Equal(["10.2.0.1", "10.2.0.2", "10.2.0.10"], await AddressesAsync(new Proto.DeviceFilter())); // IPv4 in number order
        Assert.Equal(["10.2.0.1", "10.2.0.10"], await AddressesAsync(new Proto.DeviceFilter { Models = { "M3085-V" } })); // any case
        Assert.Equal(["10.2.0.10"], await AddressesAsync(new Proto.DeviceFilter { Tags = { "ptz" }, Statuses = { Proto.DeviceStatus.Ok } }));
        Assert.Equal(["10.2.0.1"], await AddressesAsync(new Proto.DeviceFilter { FirmwareBelow = "12" }));
        Assert.Equal(["10.2.0.2"], await AddressesAsync(new Proto.DeviceFilter { FirmwareAtLeast = "12.10" }));
        Assert.Equal(["10.2.0.2"], await AddressesAsync(new Proto.DeviceFilter { Search = "YARD" })); // the UPnP name
        Assert.Equal(["10.2.0.1"], await AddressesAsync(new Proto.DeviceFilter { Search = "lobby-" })); // the host name
        Assert.Equal(["10.2.0.2"], await AddressesAsync(new Proto.DeviceFilter { Categories = { Proto.DeviceCategory.Speaker } }));
        Assert.Equal(3, (await AddressesAsync(new Proto.DeviceFilter { Sites = { "This server" } })).Count);

        var counts = await devices.SearchAsync(new Proto.SearchDevicesRequest { GroupBy = "tag" });
        Assert.Equal(3, counts.Total);
        Assert.Empty(counts.Devices);
        Assert.Equal([("Lobby", 2), ("No tag", 1), ("PTZ", 1)], counts.Counts.Select(c => (c.Value, c.Count)));
        Assert.Equal(3, (await devices.SearchAsync(new Proto.SearchDevicesRequest())).Total); // the count only

        var bad = await Assert.ThrowsAsync<RpcException>(async () => await devices.SearchAsync(new Proto.SearchDevicesRequest { Sort = "colour" }));
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await devices.SearchAsync(new Proto.SearchDevicesRequest { Limit = 501 }))).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument, (await Assert.ThrowsAsync<RpcException>(async () => await devices.SearchAsync(new Proto.SearchDevicesRequest { GroupBy = "colour" }))).StatusCode);
        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await devices.SearchAsync(new Proto.SearchDevicesRequest { Filter = new Proto.DeviceFilter { Ids = { Guid.NewGuid().ToString() } } }));
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task PagesFollowTheLastRowsSortKeyAndIdAndOnlyTheDetailsCarryTheApis()
    {
        await using var host = await TestServerHost.StartAsync();
        for (var i = 1; i <= 7; i++)
        {
            await DeviceServiceTests.AddDeviceAsync(host, $"10.3.0.{i}", 4100 + i);
        }

        var devices = new Proto.DeviceService.DeviceServiceClient(host.Invoker);
        var seen = new List<string>();
        var request = new Proto.SearchDevicesRequest { Sort = "-address", Limit = 3 };
        while (true)
        {
            var page = await devices.SearchAsync(request);
            Assert.Equal(7, page.Total);
            Assert.Equal(7 - seen.Count, page.AfterCursor);
            Assert.Equal(page.Devices.Count, page.SortKeys.Count);
            Assert.All(page.Devices, d => Assert.Empty(d.Apis));
            seen.AddRange(page.Devices.Select(d => d.Address));
            if (page.Devices.Count < 3)
            {
                break;
            }

            request = new Proto.SearchDevicesRequest { Sort = "-address", Limit = 3, AfterKey = page.SortKeys[^1], AfterId = page.Devices[^1].Id };
        }

        Assert.Equal(Enumerable.Range(1, 7).Reverse().Select(i => $"10.3.0.{i}"), seen);
    }
}
