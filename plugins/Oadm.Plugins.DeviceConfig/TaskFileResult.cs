using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>
/// The file a read-only task makes for an AI agent (Export configuration, Create configuration file): the task's result file
/// (<see cref="ITaskExecutionContext.SaveResultFileAsync"/>, one per task: an agent's download link, the assistant's file card)
/// and a line in the task's log with its name, size and SHA-256. A host that keeps no result files (an older one, a task the
/// system started) gets the text in the log instead, in parts that fit one log entry.
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

    /// <summary>The media type of the files (ADM configuration files and exports are text).</summary>
    public const string ContentType = "text/plain";

    /// <summary>
    /// Keeps the file as the task's result and writes its line into the task log; on a host without result files the text
    /// follows in the log (<see cref="Log"/>).
    /// </summary>
    public static async Task SaveAsync(ITaskExecutionContext ctx, string name, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(content);
        if (content.LongLength <= TaskResultFile.MaxBytes)
        {
            try
            {
                using var stream = new MemoryStream(content, writable: false);
                await ctx.SaveResultFileAsync(name, ContentType, stream, ct).ConfigureAwait(false);
                ctx.Log(TaskLogLevel.Info, string.Create(CultureInfo.InvariantCulture,
                    $"File {name}: {content.Length:N0} bytes, SHA-256 {Convert.ToHexString(SHA256.HashData(content))}. It is the task's result file."));
                return;
            }
            catch (NotSupportedException)
            {
                // a host that keeps no result files: the text goes into the log
            }
        }

        Log(ctx, name, content);
    }

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
