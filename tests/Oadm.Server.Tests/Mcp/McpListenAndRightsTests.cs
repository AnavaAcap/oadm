using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Sdk.Network;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// Found in the first run against a real server (2026-11-15): Listen on offers only interfaces an agent can reach, and a role
/// that may run no task gets no task or file tool.
/// </summary>
public sealed class McpListenAndRightsTests
{
    /// <summary>An Ethernet card, one that is down, a filter driver without an address, a tunnel with only a link-local address and the loopback.</summary>
    private sealed class Cards : IServerNetworkInterfaces
    {
        public IReadOnlyList<ServerNetworkInterface> List() =>
        [
            new("eth", "Ethernet", "Intel", [IPAddress.Parse("10.0.0.17")], true, false),
            new("down", "Ethernet 2", "Realtek", [IPAddress.Parse("192.168.5.2")], false, false),
            new("filter", "Local Area Connection* 9-QoS Packet Scheduler-0000", "Filter", [], true, false),
            new("tunnel", "Teredo", "Teredo", [IPAddress.Parse("fe80::1%5")], true, false),
            new("lo", "Loopback", "Loopback", [IPAddress.Loopback, IPAddress.IPv6Loopback], true, true),
        ];
    }

    [Fact]
    public async Task ListenOnOffersOnlyInterfacesThatAreUpAndHaveAnAddressAndTheLoopback()
    {
        await using var server = await McpTestServer.StartAsync(turnOn: false, configureServices: s =>
        {
            s.RemoveAll<IServerNetworkInterfaces>();
            s.AddSingleton<IServerNetworkInterfaces>(new Cards());
        });

        var state = await server.McpService().GetAsync(new Proto.Empty());

        Assert.Equal([InterfaceOptions.AllId, "eth", "lo"], state.Interfaces.Select(i => i.Id));
    }

    [Fact]
    public async Task ARoleThatMayRunNoTaskGetsNoTaskOrFileTool()
    {
        await using var server = await McpTestServer.StartAsync();
        var viewer = await server.CreateKeyAsync(name: "viewer", roleId: BuiltInRoles.ViewerId.ToString());
        var adminKey = await server.CreateKeyAsync(name: "admin", roleId: BuiltInRoles.AdministratorId.ToString());

        var seen = await server.ToolNamesAsync(viewer);
        foreach (var name in new[] { "oadm_tasks_catalog", "oadm_task_preview", "oadm_task_run", "oadm_files_upload_link", "oadm_files_list", "oadm_files_delete_preview", "oadm_files_delete_run" })
        {
            Assert.DoesNotContain(name, seen);
        }

        Assert.Contains("oadm_devices_search", seen);
        var adminTools = await server.ToolNamesAsync(adminKey); // a role with task rights (this server loads no task plugin, so only all rights)
        Assert.Contains("oadm_task_preview", adminTools);
        Assert.Contains("oadm_files_upload_link", adminTools);

        var refused = await server.CallToolAsync(viewer, "oadm_files_upload_link", new System.Text.Json.Nodes.JsonObject { ["name"] = "a.bin", ["size"] = 10 });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains(TaskRunTools.NoTaskRight, refused.ToJsonString(), StringComparison.Ordinal);
    }
}
