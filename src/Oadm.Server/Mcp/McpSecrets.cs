using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Oadm.Core.Auth;
using Oadm.Server.Tools;

namespace Oadm.Server.Mcp;

/// <summary>
/// The secret links of MCP (docs/spec/mcp.md "Secrets"): <c>PUT /secrets/&lt;token&gt;</c> with the same access key, once, within
/// the preview's 10 minutes, a body of at most <see cref="ToolSecrets.MaxBytes"/> bytes of UTF-8 (one line end at its end is
/// dropped, so <c>read</c> and <c>printf</c> or a here-string both work). The value goes into <see cref="ToolSecrets"/> encrypted
/// and is never logged, answered or audited; the audit entry "Set secret" names the secret and the key ("MCP &lt;key name&gt;").
/// </summary>
internal sealed class McpSecrets(ToolSecrets secrets, ToolRegistry tools, AuditLog audit)
{
    public const string NotText = "Send the value as UTF-8 text.";

    public async Task ReceiveAsync(HttpContext http, ToolCaller caller, string token)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(caller);
        if (http.Request.ContentLength > ToolSecrets.MaxBytes + 2)
        {
            await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, ToolSecrets.TooLong).ConfigureAwait(false);
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = ToolSecrets.MaxBytes + 2;
        }

        var buffer = new byte[ToolSecrets.MaxBytes + 3];
        var read = 0;
        try
        {
            int n;
            while (read < buffer.Length && (n = await http.Request.Body.ReadAsync(buffer.AsMemory(read), http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                read += n;
            }
        }
        catch (BadHttpRequestException)
        {
            read = buffer.Length; // over the limit
        }

        string value;
        try
        {
            if (read > ToolSecrets.MaxBytes + 2)
            {
                await RefuseAsync(http, StatusCodes.Status413PayloadTooLarge, ToolSecrets.TooLong).ConfigureAwait(false);
                return;
            }

            try
            {
                value = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, read);
            }
            catch (DecoderFallbackException)
            {
                await RefuseAsync(http, StatusCodes.Status400BadRequest, NotText).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
        }

        value = value.EndsWith("\r\n", StringComparison.Ordinal) ? value[..^2] : value.EndsWith('\n') ? value[..^1] : value;

        // The run's own check of the value first (a passphrase policy): a wrong one is refused and the link stays.
        if (secrets.SlotOfLink(caller.BindingKey, token) is { } checkedSlot
            && await ToolSecrets.CheckAsync(checkedSlot, value, http.RequestAborted).ConfigureAwait(false) is { } refused)
        {
            await RefuseAsync(http, StatusCodes.Status400BadRequest, refused).ConfigureAwait(false);
            return;
        }

        var (slot, runTool, error) = secrets.SetByLink(caller.BindingKey, token, value);
        if (error is not null)
        {
            await RefuseAsync(http, slot is null ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest, error).ConfigureAwait(false);
            return;
        }

        var title = runTool is not null && tools.TryGet(runTool, out var tool) ? tool.Title : runTool ?? string.Empty;
        await audit.WriteAsync(caller.User.AuditUserName ?? caller.User.UserName, caller.User.ClientAddress, AuditActions.SecretSet, slot!.Label,
            "For " + title, CancellationToken.None).ConfigureAwait(false);
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(new JsonObject { ["secret"] = slot.Name, ["set"] = true }.ToJsonString(ToolAnswers.Json), http.RequestAborted).ConfigureAwait(false);
    }

    private static Task RefuseAsync(HttpContext http, int status, string message)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(new JsonObject { ["error"] = message }.ToJsonString(ToolAnswers.Json), http.RequestAborted);
    }
}
