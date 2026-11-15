using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tools;

/// <summary>
/// Files a tool's run made that hold a secret (a CA key backup, a generated shared secret; docs/spec/mcp.md "Files"): kept in
/// server memory only, for the caller who ran the tool (same user and login session), fetched once through the plugin's download
/// <see cref="Method"/>, ended after <see cref="Lifetime"/>. The answer names only a one-time token, never the bytes.
/// </summary>
internal sealed class ToolFileStore(TimeProvider? time = null)
{
    /// <summary>The download method of <see cref="ICorePluginDownloads"/> that hands out a file of the store.</summary>
    public const string Method = "toolFile";

    /// <summary>How long a file waits to be fetched.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Files waiting at most (the oldest go first).</summary>
    public const int MaxFiles = 20;

    public const string Ended = "This file was fetched already or has ended. Run the tool again.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);

    /// <summary>Keeps <paramref name="content"/> for <paramref name="caller"/> and answers the file the tool's answer names.</summary>
    public PluginToolFile Put(ICallContext caller, string fileName, string contentType, byte[] content, string auditDetail)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(content);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            Drop();
            foreach (var oldest in _files.OrderBy(f => f.Value.Ends).Take(Math.Max(0, _files.Count - MaxFiles + 1)).Select(f => f.Key).ToList())
            {
                Remove(oldest);
            }

            _files[token] = new Entry(OwnerOf(caller), fileName, content, _time.GetUtcNow() + Lifetime, auditDetail);
        }

        return new PluginToolFile(Method, new JsonObject { ["token"] = token }.ToJsonString(), fileName, contentType, content.Length);
    }

    /// <summary>The file of the payload's token for <paramref name="caller"/>, once; refused when it ended or is another caller's.</summary>
    public PluginDownload Take(ICallContext caller, string? payloadJson)
    {
        ArgumentNullException.ThrowIfNull(caller);
        string? token = null;
        try
        {
            token = JsonNode.Parse(payloadJson ?? "{}")?["token"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            // an unreadable payload is an ended file
        }

        lock (_gate)
        {
            Drop();
            if (token is null || !_files.TryGetValue(token, out var entry) || entry.Owner != OwnerOf(caller))
            {
                throw new InvalidOperationException(Ended);
            }

            _files.Remove(token);
            var copy = entry.Content.ToArray();
            CryptographicOperations.ZeroMemory(entry.Content);
            return new PluginDownload(new MemoryStream(copy, writable: false), entry.FileName, copy.Length) { AuditDetail = entry.AuditDetail };
        }
    }

    /// <summary>Files waiting now (tests).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                Drop();
                return _files.Count;
            }
        }
    }

    private static string OwnerOf(ICallContext caller) => caller.SessionId + "\n" + caller.UserName;

    private void Drop()
    {
        var now = _time.GetUtcNow();
        foreach (var ended in _files.Where(f => f.Value.Ends <= now).Select(f => f.Key).ToList())
        {
            Remove(ended);
        }
    }

    private void Remove(string token)
    {
        if (_files.Remove(token, out var entry))
        {
            CryptographicOperations.ZeroMemory(entry.Content);
        }
    }

    private sealed record Entry(string Owner, string FileName, byte[] Content, DateTimeOffset Ends, string AuditDetail);
}
