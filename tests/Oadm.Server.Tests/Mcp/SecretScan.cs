using System.Text;

using Oadm.Core.Persistence;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The security scan of a secret (docs/spec/mcp.md "Secrets", rule 4): a canary value a test sent through the secret channel
/// is in no tool answer the test collected, no audit entry, no line of the server log (Serilog's files), no task (name,
/// steps, result message, per-task log) and no file of the data folder (the database with its WAL: the task table, rules,
/// settings).
/// </summary>
internal static class SecretScan
{
    /// <summary>Asserts that <paramref name="canary"/> (and its first 12 characters) appears nowhere of the server but <paramref name="allowedFiles"/>.</summary>
    public static async Task AssertNowhereAsync(TestServerHost host, string canary, string answers, params string[] allowedFiles)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(canary);
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries
            .Select(e => string.Join('|', e.UserName, e.Action, e.Target, e.Detail)).ToList();
        var tasks = new StringBuilder();
        foreach (var task in (await host.Tasks.ListAsync(new Proto.ListTasksRequest())).Tasks)
        {
            tasks.Append(task).Append('\n');
            tasks.Append(await host.Tasks.GetLogAsync(new Proto.TaskIdRequest { TaskId = task.Id })).Append('\n');
        }

        var paths = host.Get<OadmPaths>();
        var logs = McpSecretTests.ReadLogs(paths.LogsDirectory);
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var file in Directory.EnumerateFiles(paths.DataDirectory, "*", SearchOption.AllDirectories))
        {
            if (allowedFiles.Any(a => file.EndsWith(a, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                files.Add((file, copy.ToArray()));
            }
            catch (IOException)
            {
                // a file that went away meanwhile
            }
        }

        foreach (var part in new[] { canary, canary.Length > 12 ? canary[..12] : canary })
        {
            Assert.DoesNotContain(part, answers, StringComparison.Ordinal);
            Assert.DoesNotContain(audit, e => e.Contains(part, StringComparison.Ordinal));
            Assert.DoesNotContain(part, logs, StringComparison.Ordinal);
            Assert.DoesNotContain(part, tasks.ToString(), StringComparison.Ordinal);
            foreach (var encoded in new[] { Encoding.UTF8.GetBytes(part), Encoding.Unicode.GetBytes(part) })
            {
                foreach (var (path, bytes) in files)
                {
                    Assert.False(bytes.AsSpan().IndexOf(encoded) >= 0, "The secret is in " + path);
                }
            }
        }
    }
}
