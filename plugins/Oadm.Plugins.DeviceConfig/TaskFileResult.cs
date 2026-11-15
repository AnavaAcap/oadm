using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// The file a read-only task makes for an AI agent (Export configuration, Create configuration file): for now its result is in
/// the task's log, a line with its name, size and SHA-256 and then the text in parts that fit one log entry.
/// TODO(download-link): hand the bytes to a download link once the tools offer them, and keep only the first line here.
/// </summary>
public static class TaskFileResult
{
    /// <summary>Characters of one part (a log entry holds at most 2,000).</summary>
    public const int PartLength = 1900;

    /// <summary>Most parts one file writes (the task log keeps 1,000 entries).</summary>
    public const int MaxParts = 400;

    /// <summary>"Configuration - AXIS P3265-V 12.11.77.cfg, 24 KB".</summary>
    public static string Summary(string name, int bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{name}, {Math.Max(1, (bytes + 512) / 1024):N0} KB");

    /// <summary>Writes the file's line and its text into the task log; a file too large for the log gets the line only.</summary>
    public static void Log(ITaskExecutionContext ctx, string name, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(content);
        var text = new UTF8Encoding(false).GetString(content).TrimStart('﻿');
        var parts = (text.Length + PartLength - 1) / PartLength;
        var sha = Convert.ToHexString(SHA256.HashData(content));
        if (parts > MaxParts)
        {
            ctx.Log(TaskLogLevel.Warning, string.Create(CultureInfo.InvariantCulture,
                $"File {name}: {content.Length:N0} bytes, SHA-256 {sha}. It is too large for the task log; create it in OADM."));
            return;
        }

        ctx.Log(TaskLogLevel.Info, string.Create(CultureInfo.InvariantCulture,
            $"File {name}: {content.Length:N0} bytes, SHA-256 {sha}. The text follows in {parts:N0} {(parts == 1 ? "part" : "parts")}."));
        for (var i = 0; i < parts; i++)
        {
            var start = i * PartLength;
            ctx.Log(TaskLogLevel.Info, string.Concat(string.Create(CultureInfo.InvariantCulture, $"Part {i + 1} of {parts}: "),
                text.AsSpan(start, Math.Min(PartLength, text.Length - start))));
        }
    }
}
