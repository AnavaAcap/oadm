using Grpc.Core;

using Oadm.Server.Auth;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>
/// The methods whose answers grow with the device count are for the server's own tools only (docs/spec/grpc.md "In process
/// only"): a client over the network is refused, also an administrator, before anything is read.
/// </summary>
public sealed class InProcessOnlyTests
{
    [Fact]
    public async Task ANetworkClientIsRefusedTheMethodsThatAnswerEveryId()
    {
        await using var host = await TestServerHost.StartAsync();
        var filter = new Proto.DeviceFilter();
        var calls = new Dictionary<string, Func<Task>>
        {
            ["Resolve"] = async () => await new Proto.DeviceService.DeviceServiceClient(host.Invoker).ResolveAsync(new Proto.ResolveDevicesRequest { Filter = filter, Shown = 5 }),
            ["Plan"] = async () => await new Proto.TaskService.TaskServiceClient(host.Invoker).PlanAsync(new Proto.PlanTaskRequest { PluginId = "oadm.restart", Filter = filter }),
            ["PlanDeviceTags"] = async () => await new Proto.TagService.TagServiceClient(host.Invoker).PlanDeviceTagsAsync(new Proto.PlanDeviceTagsRequest { Filter = filter, Add = { "Lobby" } }),
        };

        foreach (var (name, call) in calls)
        {
            var refused = await Assert.ThrowsAsync<RpcException>(call);
            Assert.True(refused.StatusCode == StatusCode.PermissionDenied, $"{name}: {refused.StatusCode}");
            Assert.Equal(AccessPolicy.InProcessOnlyMessage, refused.Status.Detail);
        }
    }

    [Fact]
    public void OnlyTheThreeMethodsAreInProcessOnly()
    {
        string[] expected = ["/oadm.v1.DeviceService/Resolve", "/oadm.v1.TagService/PlanDeviceTags", "/oadm.v1.TaskService/Plan"];
        foreach (var path in expected)
        {
            Assert.True(AccessPolicy.For(path).InProcessOnly, path);
        }

        Assert.False(AccessPolicy.For("/oadm.v1.DeviceService/Search").InProcessOnly);
        Assert.False(AccessPolicy.For("/oadm.v1.TaskService/Run").InProcessOnly);
    }
}
