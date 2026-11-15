using Oadm.Contracts.Assistant;
using Oadm.Server.LiveView;
using Oadm.Server.Mcp;
using Oadm.Server.Tools;
using Oadm.Sites.Transport;

namespace Oadm.Server.Assistant;

/// <summary>
/// The limits of the assistant's channel (docs/spec/assistant.md "Limits"), the same as MCP's per access key, here per login
/// session: the model's tool calls that count as a change (a call that acts outside OADM, like the network scan of
/// <c>oadm_devices_add_preview</c>) at most <see cref="ChangesPerMinute"/> a minute, and no camera picture once the user's
/// pictures of the minute are used up (<see cref="SnapshotAudit"/>). One tool call at a time per session: the loop runs them
/// in turn. A Run is the user's click and counts against nothing here.
/// </summary>
internal sealed class AssistantLimits(TimeProvider time, SnapshotAudit snapshots)
{
    public const int ChangesPerMinute = McpAccess.ChangesPerMinute;

    private readonly AddressRateLimiter _changes = new(ChangesPerMinute, total: 0, TimeSpan.FromMinutes(1), time);

    /// <summary>Counts the call; the refusal text when a limit is reached (nothing counted then), else null.</summary>
    public string? Refusal(OadmTool tool, ToolCaller caller)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(caller);
        if (tool.CountsAsPicture && snapshots.IsLimited(caller.User.UserId))
        {
            return AssistantTexts.TooManyPictures;
        }

        return tool.CountsAsChange && !_changes.TryAcquire(caller.BindingKey) ? AssistantTexts.TooManyChanges : null;
    }
}
