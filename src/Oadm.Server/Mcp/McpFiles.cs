using System.Globalization;
using System.Text.Json.Nodes;

using Grpc.Core;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Oadm.Core.Auth;
using Oadm.Server.Auth;
using Oadm.Server.Files;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Mcp;

/// <summary>
/// The upload links of MCP (docs/spec/mcp.md "Files"): <c>PUT /files/&lt;token&gt;</c> with the same access key, once, within 10
/// minutes, exactly the size the link was made for. The file goes through <c>FileService.Upload</c> in process as the key's
/// caller (its limits, its owner: the tasks of this key read it), never through the model; audited as "&lt;user&gt; via access
/// key &lt;name&gt;". A Read only key never sends a file, also with a link.
/// </summary>
internal sealed class McpFiles(ToolHandles handles, InProcessCalls calls, AuditLog audit)
{
    /// <summary>The size of one data chunk, as the client sends them.</summary>
    public const int ChunkBytes = 256 * 1024;

    public const string WrongSize = "The file must have the size the link was made for.";

    public async Task ReceiveAsync(HttpContext http, ToolCaller caller, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.ReadOnly)
        {
            await RefuseAsync(http, StatusCodes.Status403Forbidden, McpAccess.ReadOnlyKey).ConfigureAwait(false);
            return;
        }

        if (handles.TakeUpload(caller.BindingKey, token) is not { } upload)
        {
            await RefuseAsync(http, StatusCodes.Status404NotFound, ToolHandles.LinkEnded).ConfigureAwait(false);
            return;
        }

        if (http.Request.ContentLength != upload.Size)
        {
            await RefuseAsync(http, StatusCodes.Status400BadRequest, WrongSize).ConfigureAwait(false);
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = upload.Size;
        }

        Proto.UploadedFileInfo file;
        try
        {
            file = await calls.CallAsync<FileGrpcService, IAsyncStreamReader<Proto.UploadChunk>, Proto.UploadedFileInfo>(
                caller.User, TaskRunTools.UploadPath, new BodyChunks(http.Request.Body, upload), (s, r, c) => s.Upload(r, c), http.RequestAborted).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode != StatusCode.Cancelled)
        {
            var status = ex.StatusCode switch
            {
                StatusCode.ResourceExhausted => StatusCodes.Status413PayloadTooLarge,
                StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
                StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError,
            };
            await RefuseAsync(http, status, string.IsNullOrWhiteSpace(ex.Status.Detail) ? ToolAnswers.Failed : ex.Status.Detail).ConfigureAwait(false);
            return;
        }

        await audit.WriteAsync(caller.User.AuditUserName ?? caller.User.UserName, caller.User.ClientAddress, AuditActions.FileUploaded, file.Name,
            string.Create(CultureInfo.InvariantCulture, $"{file.Size:N0} bytes"), CancellationToken.None).ConfigureAwait(false);
        http.Response.ContentType = "application/json";
        var answer = new JsonObject { ["file_id"] = file.Id, ["name"] = file.Name, ["size"] = file.Size, ["sha256"] = file.Sha256 };
        await http.Response.WriteAsync(answer.ToJsonString(ToolAnswers.Json), http.RequestAborted).ConfigureAwait(false);
    }

    private static Task RefuseAsync(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(new JsonObject { ["error"] = message }.ToJsonString(ToolAnswers.Json), http.RequestAborted);
    }

    /// <summary>The request body as the chunks of a client's upload: the header (name, size), then data.</summary>
    private sealed class BodyChunks(Stream body, PlannedUpload upload) : IAsyncStreamReader<Proto.UploadChunk>
    {
        private readonly byte[] _buffer = new byte[ChunkBytes];
        private bool _headerSent;

        public Proto.UploadChunk Current { get; private set; } = new();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (!_headerSent)
            {
                _headerSent = true;
                Current = new Proto.UploadChunk { Header = new Proto.UploadHeader { Name = upload.Name, Size = upload.Size } };
                return true;
            }

            var read = 0;
            while (read < _buffer.Length)
            {
                var n = await body.ReadAsync(_buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            if (read == 0)
            {
                return false;
            }

            Current = new Proto.UploadChunk { Data = Google.Protobuf.ByteString.CopyFrom(_buffer, 0, read) };
            return true;
        }
    }
}
