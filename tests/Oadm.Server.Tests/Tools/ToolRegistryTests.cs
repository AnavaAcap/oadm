using System.Reflection;
using System.Text.Json;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;
using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.Tools;

/// <summary>
/// The tools independent of MCP (docs/spec/mcp.md "Tools and MCP"): one registry the server calls in process with a caller
/// object and nothing of a request, so a later built-in assistant calls the same tools; MCP is only an adapter on top.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public async Task EveryToolLosesNativeApprovalAuthorityEvenWithTheSignedInUsersSession()
    {
        await using var host = await TestServerHost.StartAsync();
        var caller = await SignedInCallerAsync(host, "native-session");
        caller = caller with { User = caller.User with { CanApprove = true } };
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!.AsObject();
        var probe = new OadmTool("oadm_test_approval", "Test", "Test", schema, schema, [], call =>
        {
            Assert.False(call.Caller.User.CanApprove);
            return Task.FromResult(new System.Text.Json.Nodes.JsonObject());
        }) { Gate = toolCaller => toolCaller.User.CanApprove ? "Tool still has native approval authority" : null };
        var registry = new ToolRegistry([probe], host.Get<Oadm.Server.Auth.InProcessCalls>());
        var result = await registry.CallAsync(caller, probe.Name, null, CancellationToken.None);
        Assert.False(result.IsError, result.Error);
        Assert.True(caller.User.CanApprove); // the native session is unchanged outside tool execution
    }

    /// <summary>The caller a channel without access keys makes, e.g. the logged-in user of the client.</summary>
    private static async Task<ToolCaller> SignedInCallerAsync(TestServerHost host, string bindingKey)
    {
        var admin = (await host.Get<UserStore>().FindByNameAsync(TestServerHost.AdminUserName, CancellationToken.None))!;
        var permissions = await host.Get<RoleStore>().PermissionsOfAsync(admin.RoleId, CancellationToken.None);
        var devices = await host.Get<DeviceViewFactory>().CallerDevicesAsync(new DeviceScope(true, null), CancellationToken.None);
        var user = new AuthenticatedCaller(admin.Id, admin.UserName, admin.RoleId, permissions, "127.0.0.1", "Assistant", bindingKey) { Devices = devices };
        return new ToolCaller(user, bindingKey);
    }

    [Fact]
    public async Task TwoToolsRunInProcessWithACallerObjectAndNoRequest()
    {
        await using var host = await TestServerHost.StartAsync(); // the MCP server stays off: no port, no access key
        for (var i = 1; i <= 3; i++)
        {
            await DeviceServiceTests.AddDeviceAsync(host, $"10.11.0.{i}", 4200 + i);
        }

        var registry = host.Get<ToolRegistry>();
        var caller = await SignedInCallerAsync(host, "session-one");
        Assert.Contains(registry.For(caller), t => t.Name == "oadm_devices_search");

        var info = await registry.CallAsync(caller, "oadm_server_info", null, CancellationToken.None);
        Assert.False(info.IsError, info.Error);
        Assert.Equal(3, info.Answer!["devices"]!["count"]!.GetValue<int>());
        Assert.Null(info.Answer["access_key"]); // no key: the channel tells none, and no limits of its own
        Assert.Null(info.Answer["limits"]!["calls_per_second"]);

        var first = await registry.CallAsync(caller, "oadm_devices_search", JsonDocument.Parse("""{"limit": 2}""").RootElement, CancellationToken.None);
        Assert.False(first.IsError, first.Error);
        Assert.Equal(3, first.Answer!["total"]!.GetValue<int>());
        var cursor = first.Answer["next_cursor"]!.GetValue<string>();
        var next = await registry.CallAsync(caller, "oadm_devices_search", JsonDocument.Parse($$"""{"limit": 2, "cursor": "{{cursor}}"}""").RootElement, CancellationToken.None);
        Assert.Equal("10.11.0.3", Assert.Single(next.Answer!["devices"]!.AsArray())!["address"]!.GetValue<string>());

        // Cursors are bound to the caller's binding key; arguments are checked against the schema like over MCP.
        var other = await SignedInCallerAsync(host, "session-two");
        var foreign = await registry.CallAsync(other, "oadm_devices_search", JsonDocument.Parse($$"""{"cursor": "{{cursor}}"}""").RootElement, CancellationToken.None);
        Assert.Equal(ToolCursors.NotValid, foreign.Error);
        var invalid = await registry.CallAsync(caller, "oadm_devices_search", JsonDocument.Parse("""{"limit": 0}""").RootElement, CancellationToken.None);
        Assert.Equal(ToolAnswers.FieldsMessage, invalid.Error);
        Assert.Equal("limit", Assert.Single(invalid.Fields).Field);

        // The registry needs no permission of any channel: the role alone decides (an Operator lists the device search but not
        // the audit log, which is the Administrator's).
        var operatorRole = caller with { User = caller.User with { RoleId = BuiltInRoles.OperatorId, Permissions = await host.Get<RoleStore>().PermissionsOfAsync(BuiltInRoles.OperatorId, CancellationToken.None) } };
        Assert.Contains(registry.For(operatorRole), t => t.Name == "oadm_devices_search");
        Assert.Contains(registry.For(operatorRole), t => t.Name == "oadm_server_info");
        Assert.DoesNotContain(registry.For(operatorRole), t => t.Name == "oadm_logs_audit");
        var refused = await registry.CallAsync(operatorRole, "oadm_logs_audit", null, CancellationToken.None);
        Assert.True(refused.IsError);
    }

    /// <summary>Tools that change something without a preview: they only hand out a one-time link, the change is the file sent to it.</summary>
    private static readonly string[] LinksWithoutPreview = ["oadm_files_upload_link"];

    [Fact]
    public async Task PreviewAndRunStayTwoTools()
    {
        await using var host = await TestServerHost.StartAsync();
        var tools = host.Get<ToolRegistry>().All.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var runs = tools.Values.Where(t => !t.ReadsOnly && !LinksWithoutPreview.Contains(t.Name)).ToList();
        Assert.NotEmpty(runs);
        foreach (var run in runs)
        {
            // Every change is a run tool that takes only the handle of its preview...
            Assert.EndsWith("_run", run.Name, StringComparison.Ordinal);
            var properties = run.Input["properties"]!.AsObject().Select(p => p.Key).ToList();
            Assert.Equal(["handle"], properties);

            // ...and its preview is a separate tool that only reads and answers a handle.
            var previewName = run.Name[..^"_run".Length] + "_preview";
            Assert.True(tools.TryGetValue(previewName, out var preview), $"{run.Name} has no {previewName}");
            Assert.True(preview!.ReadsOnly, previewName);
            Assert.True(preview.Output["properties"]!.AsObject().ContainsKey("handle"), $"{previewName} answers no handle");
        }

        // No tool both reads a plan and changes: a preview never writes, a run never takes anything but the handle.
        Assert.All(tools.Values.Where(t => t.Name.EndsWith("_preview", StringComparison.Ordinal)), t => Assert.True(t.ReadsOnly, t.Name));
    }

    /// <summary>The namespace of the tool layer: the registry and every tool.</summary>
    private const string ToolsNamespace = "Oadm.Server.Tools";

    [Fact]
    public void TheToolsKnowNothingOfMcpOrOfAnHttpRequest()
    {
        // In the source: no MCP SDK, no ASP.NET, nothing of the MCP adapter.
        var sources = Directory.EnumerateFiles(TestRepository.Path("src", "Oadm.Server", "Tools"), "*.cs", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var banned in new[] { "ModelContextProtocol", "Microsoft.AspNetCore", "HttpContext", "HttpRequest", "Oadm.Server.Mcp", "McpAccess", "McpEndpoint" })
            {
                Assert.False(text.Contains(banned, StringComparison.Ordinal), $"{Path.GetFileName(file)} names {banned}");
            }
        }

        // In the compiled types: no field, property, parameter, return or base type of the MCP SDK, of ASP.NET or of the adapter.
        var types = typeof(ToolRegistry).Assembly.GetTypes().Where(t => (t.Namespace ?? string.Empty) == ToolsNamespace).ToList();
        Assert.Contains(types, t => t.Name == "DeviceTools");
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in types)
        {
            var used = new List<Type>();
            used.AddRange(type.GetInterfaces());
            if (type.BaseType is { } baseType)
            {
                used.Add(baseType);
            }

            used.AddRange(type.GetFields(All).Select(f => f.FieldType));
            used.AddRange(type.GetProperties(All).Select(p => p.PropertyType));
            foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
            {
                used.AddRange(method.GetParameters().Select(p => p.ParameterType));
                if (method is MethodInfo info)
                {
                    used.Add(info.ReturnType);
                }
            }

            foreach (var used1 in used.SelectMany(Flatten))
            {
                var assembly = used1.Assembly.GetName().Name ?? string.Empty;
                Assert.False(assembly.StartsWith("ModelContextProtocol", StringComparison.Ordinal) || assembly.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                    || (used1.Namespace ?? string.Empty).StartsWith("Oadm.Server.Mcp", StringComparison.Ordinal),
                    $"{type.FullName} uses {used1.FullName}");
            }
        }
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        var element = type.HasElementType ? type.GetElementType()! : type;
        yield return element;
        if (element.IsGenericType)
        {
            foreach (var argument in element.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }
}
