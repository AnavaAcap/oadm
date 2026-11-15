using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Tools;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15): Delete all of the Enrollments tab as a preview
/// and a run, with the rights of its page method (Administrator, "manage", users with all devices, audited).
/// </summary>
internal static class EstPageTools
{
    public const string NoEnrollments = "The enrollments list is empty.";

    public static PluginPageTools<EstPlugin> Set { get; } = new PluginPageTools<EstPlugin>(EstTools.Json)
        .Change<NoArgs, ClearPlan, ClearResult>(
            new("enrollments_clear", "Delete all enrollments", "Deletes every entry of the EST server's enrollments list. Issued certificates stay valid.")
            {
                Destructive = true,
            },
            EstMethods.ClearEnrollments,
            (plugin, _, _, _) =>
            {
                var count = Running(plugin).Enrollments(null).Count;
                return count == 0 ? throw new InvalidOperationException(NoEnrollments) : Task.FromResult(new ClearPlan(count));
            },
            (plugin, _, _, plan, _) =>
            {
                Running(plugin).ClearEnrollments();
                return Task.FromResult(new ClearResult(plan.WillChange));
            });

    private static Serving.EstService Running(EstPlugin plugin) => plugin.Service ?? throw new InvalidOperationException(EstPlugin.NotRunning);

    internal sealed record NoArgs;

    internal sealed record ClearPlan(int Entries) : PluginToolPlan(Entries, "enrollment");

    internal sealed record ClearResult(int Deleted);
}
