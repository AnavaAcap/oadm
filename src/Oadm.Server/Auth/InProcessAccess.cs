using System.Security.Cryptography;

using Grpc.Core;

using Microsoft.AspNetCore.Http;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Auth;

/// <summary>
/// Session tokens for code that hosts the server in its own process (tests, hardware tests through the in-process
/// server): creates the user with a random password when it does not exist and returns a token for it, without a
/// login over gRPC.
/// </summary>
public static class InProcessAccess
{
    public static Task<string> CreateTokenAsync(IServiceProvider services, string userName = "admin", UserRole role = UserRole.Admin, CancellationToken ct = default) =>
        CreateTokenAsync(services, userName, BuiltInRoles.IdOf(role), ct);

    /// <summary>Like <see cref="CreateTokenAsync(IServiceProvider, string, UserRole, CancellationToken)"/> with any role.</summary>
    public static async Task<string> CreateTokenAsync(IServiceProvider services, string userName, Guid roleId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var users = services.GetRequiredService<UserStore>();
        var user = await users.FindByNameAsync(userName, ct).ConfigureAwait(false)
            ?? await users.CreateAsync(userName, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)), roleId, onlyIfNoUsers: false, ct).ConfigureAwait(false);
        return await services.GetRequiredService<AuthTokenStore>().CreateAsync(user, remember: false, "in-process", ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The server's gRPC services called in process as an authenticated caller (docs/spec/mcp.md rule 1: the MCP server's tools
/// are a client): the rule of <see cref="AccessPolicy"/> for the method, the caller in the call's user state (so services get
/// their <see cref="DeviceView"/> from it) and in <see cref="CallerContext"/>, and the same answers for unknown devices and
/// refused plugin code as <see cref="AuthInterceptor"/> gives over the network. Only unary methods; nothing is serialized.
/// </summary>
public sealed class InProcessCalls(IServiceProvider services, RoleStore roles, Updates.UpdateGate? update = null)
{
    /// <summary>"/oadm.v1.DeviceService/List": the path of <paramref name="method"/> of <paramref name="service"/> (checked against the contract).</summary>
    public static string PathOf(Google.Protobuf.Reflection.ServiceDescriptor service, string method)
    {
        ArgumentNullException.ThrowIfNull(service);
        _ = service.FindMethodByName(method) ?? throw new ArgumentException($"{service.FullName} has no method {method}.", nameof(method));
        return "/" + service.FullName + "/" + method;
    }

    /// <summary>True when <paramref name="caller"/> may call the method <paramref name="path"/> (the interceptor's check).</summary>
    public static bool Allows(string path, AuthenticatedCaller caller) => AccessPolicy.Allows(AccessPolicy.For(path), caller);

    /// <summary>The refusal of <paramref name="path"/> for <paramref name="caller"/> ("Your role does not allow: ...").</summary>
    public string DeniedMessage(string path, AuthenticatedCaller caller) => AccessPolicy.DeniedMessage(AccessPolicy.For(path), roles.Catalog, caller);

    /// <summary>
    /// Calls the method <paramref name="path"/> of the service <typeparamref name="TService"/> (made for this call from the
    /// server's services, like gRPC does) as <paramref name="caller"/>. Throws the <see cref="RpcException"/> a network client
    /// would get (PERMISSION_DENIED, NOT_FOUND, INVALID_ARGUMENT ...). Nothing of an HTTP request reaches it: a service that
    /// asks for the call's connection gets one with the caller's address (<see cref="AuthInterceptor.ClientAddressOf"/>).
    /// </summary>
    public async Task<TResponse> CallAsync<TService, TRequest, TResponse>(
        AuthenticatedCaller caller, string path, TRequest request, Func<TService, TRequest, ServerCallContext, Task<TResponse>> call, CancellationToken ct)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(call);
        var rule = AccessPolicy.For(path);
        if (update?.ClosedFor is { } updatingTo && !Updates.UpdateGate.StaysOpen(path))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, Contracts.Updates.UpdateTexts.Updating(updatingTo)));
        }

        if (!AccessPolicy.Allows(rule, caller))
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AccessPolicy.DeniedMessage(rule, roles.Catalog, caller)));
        }

        var context = new InProcessCallContext(path, caller, ct);
        var service = ActivatorUtilities.CreateInstance<TService>(services);
        using var scope = CallerContext.Use(caller);
        try
        {
            return await call(service, request, context).ConfigureAwait(false);
        }
        catch (DevicesNotAvailableException ex)
        {
            throw AuthInterceptor.NotAvailable(ex);
        }
        catch (DeviceAccessRefusedException ex)
        {
            throw AuthInterceptor.Refused(ex);
        }
        finally
        {
            (service as IDisposable)?.Dispose();
        }
    }

    /// <summary>The call context of an in-process call: the method, the caller in the user state, the caller's address, no headers or deadline.</summary>
    private sealed class InProcessCallContext : ServerCallContext
    {
        /// <summary>Where Grpc.AspNetCore's <c>GetHttpContext</c> finds the HTTP request of a call made outside its pipeline.</summary>
        private const string HttpContextKey = "__HttpContext";

        private readonly string _method;
        private readonly CancellationToken _ct;
        private readonly Metadata _headers = [];
        private readonly Metadata _trailers = [];
        private readonly Dictionary<object, object> _state = [];

        public InProcessCallContext(string method, AuthenticatedCaller caller, CancellationToken ct)
        {
            _method = method;
            _ct = ct;
            _state[AuthInterceptor.CallerKey] = caller;
            var connection = new DefaultHttpContext();
            connection.Connection.RemoteIpAddress = System.Net.IPAddress.TryParse(caller.ClientAddress, out var address) ? address : null;
            _state[HttpContextKey] = connection; // the caller's address, never a request of a channel
        }

        protected override string MethodCore => _method;

        protected override string HostCore => "in-process";

        protected override string PeerCore => "in-process";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => _headers;

        protected override CancellationToken CancellationTokenCore => _ct;

        protected override Metadata ResponseTrailersCore => _trailers;

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new(null, []);

        protected override IDictionary<object, object> UserStateCore => _state;

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException("An in-process call starts no other calls.");

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
