using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

using Oadm.Plugins.LoadTest.Checking;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Tools;

/// <summary>
/// The page's actions as tools (docs/spec/mcp.md "Plugins", decision 2026-11-15), through the page's own service methods with
/// the caller: Plan (the page reads the chosen cameras without asking, "show"), Start as a preview and a run (the page asks
/// first: the plan, its refusal and the exposure warning; then the page's start with every safety of the check: at most 1,000
/// streams, batches of at most 200, the exposure always written back), Cancel (the page stops without asking) and Delete
/// check as a preview and a run, with the right "run".
/// </summary>
internal static class LoadTestPageTools
{
    public const string ChooseDevices = "Choose cameras, or set all_devices.";
    public const string NoneCanRun = "None of the chosen cameras can be checked.";

    public static PluginPageTools<LoadTestPlugin> Set { get; } = new PluginPageTools<LoadTestPlugin>(LoadTestTools.Json)
        .Read<CheckArgs, PlanAnswer>(
            new("plan", "Plan load test", "Plans a load test of the chosen cameras, or of all your cameras with all_devices: the camera streams, batches, duration and expected rates. Changes nothing.")
            {
                DeviceFilter = true,
            },
            LoadTestMethods.Plan,
            async (plugin, caller, input, ct) => PlanOf(await PlanAsync(plugin, caller, input, ct).ConfigureAwait(false), input.Args))
        .Change<CheckArgs, StartPlan, LoadTestTools.SummaryAnswer>(
            new("start", "Start load test", "Starts a load test of the chosen cameras, or of all your cameras with all_devices. The exposure of the cameras changes for a few minutes and always goes back. One check at a time.")
            {
                DeviceFilter = true,
            },
            LoadTestMethods.Start,
            CheckStartAsync,
            async (plugin, caller, input, _, ct) => LoadTestTools.SummaryOf(await plugin.RunningService.StartAsync(caller, RequestOf(input), ct).ConfigureAwait(false)))
        .Read<NoArgs, CancelAnswer>(
            new("cancel", "Cancel load test", "Cancels the running load test of your devices. The exposure goes back and the measured part is kept."),
            LoadTestMethods.Cancel,
            async (plugin, caller, _, _) => await plugin.RunningService.CancelAsync(caller).ConfigureAwait(false) is { } summary
                ? new CancelAnswer(true, LoadTestTools.SummaryOf(summary))
                : new CancelAnswer(false, null))
        .Change<RunArgs, DeletePlan, DeletedAnswer>(
            new("check_delete", "Delete load test check", "Deletes a kept load test check and its measurements.")
            {
                Destructive = true,
            },
            LoadTestMethods.DeleteRun,
            (plugin, caller, input, _) => Task.FromResult(CheckDelete(plugin, caller, input.Args)),
            async (plugin, caller, input, plan, ct) =>
            {
                await plugin.RunningService.DeleteRunAsync(caller, new RunRequest(input.Args.RunId), ct).ConfigureAwait(false);
                return new DeletedAnswer(plan.RunId);
            });

    private static async Task<StartPlan> CheckStartAsync(LoadTestPlugin plugin, ICallContext caller, PluginToolInput<CheckArgs> input, CancellationToken ct)
    {
        var plan = await PlanAsync(plugin, caller, input, ct).ConfigureAwait(false);
        if (plan.Refusal is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        if (plan.Streams == 0)
        {
            throw new InvalidOperationException(NoneCanRun);
        }

        plugin.RunningService.RefuseStartWhileRunning(caller); // the page's texts: who runs it and how far, or no user for another group
        var minutes = input.Args.PhaseMinutes ?? LoadTestInfo.DefaultPhaseMinutes;
        return new StartPlan(
            PlanOf(plan, input.Args),
            [ExposureWarning(minutes * plan.Batches)]);
    }

    /// <summary>The page's plan of the arguments; the numbers checked first with the page's texts.</summary>
    private static Task<CheckPlan> PlanAsync(LoadTestPlugin plugin, ICallContext caller, PluginToolInput<CheckArgs> input, CancellationToken ct)
    {
        var request = RequestOf(input);
        return plugin.RunningService.PlanAsync(caller, new PlanRequest(request.DeviceIds, request.BatchSize, request.PhaseMinutes, request.AllDevices), ct);
    }

    private static StartRequest RequestOf(PluginToolInput<CheckArgs> input)
    {
        var args = input.Args;
        var errors = new List<KeyValuePair<string, string>>();
        var batch = args.BatchSize ?? LoadTestInfo.DefaultBatchSize;
        var minutes = args.PhaseMinutes ?? LoadTestInfo.DefaultPhaseMinutes;
        if (batch is < 1 or > LoadTestInfo.MaxBatchSize)
        {
            errors.Add(new("batch_size", string.Create(CultureInfo.InvariantCulture, $"Choose 1 to {LoadTestInfo.MaxBatchSize} cameras at once.")));
        }

        if (!LoadTestInfo.PhaseMinutes.Contains(minutes))
        {
            errors.Add(new("phase_minutes", "Choose 1, 2 or 5 minutes per step."));
        }

        if (!args.AllDevices && input.Devices.Ids.Count == 0)
        {
            errors.Add(new("filter", ChooseDevices));
        }
        else if (!args.AllDevices && input.Devices.Left > 0)
        {
            errors.Add(new("filter", string.Create(CultureInfo.InvariantCulture, $"Choose at most {PluginTool.MaxDevices} cameras, or set all_devices.")));
        }

        if (errors.Count > 0)
        {
            throw new PluginToolInputException(errors);
        }

        return new StartRequest(args.AllDevices ? [] : input.Devices.Ids, batch, minutes, args.AllDevices);
    }

    private static DeletePlan CheckDelete(LoadTestPlugin plugin, ICallContext caller, RunArgs args)
    {
        if (args.RunId == Guid.Empty)
        {
            throw new PluginToolInputException("run_id", LoadTestTools.EnterRun);
        }

        var list = plugin.RunningService.ListRuns(caller);
        if (list.RunningId == args.RunId)
        {
            throw new InvalidOperationException("Cancel the check first.");
        }

        var run = list.Runs.FirstOrDefault(r => r.RunId == args.RunId) ?? throw new KeyNotFoundException("The check was deleted.");
        return new DeletePlan(
            run.RunId.ToString(),
            run.StartedUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            run.StartedBy,
            run.Verdict);
    }

    private static PlanAnswer PlanOf(CheckPlan plan, CheckArgs args) => new(
        plan.Streams,
        plan.Batches,
        args.BatchSize ?? LoadTestInfo.DefaultBatchSize,
        args.PhaseMinutes ?? LoadTestInfo.DefaultPhaseMinutes,
        (int)Math.Ceiling(plan.Duration.TotalMinutes),
        Math.Round(plan.PeakMbps, 1),
        Math.Round(plan.LowLightMbps, 1),
        plan.LeftOut.Count,
        [.. plan.LeftOut.Take(MaxLeftOut).Select(l => new LeftOutAnswer(l.DeviceId.ToString(), l.Name, l.Reason))],
        plan.Refusal);

    /// <summary>The exposure warning of the page's confirmation (docs/spec/load-test.md "Confirmation").</summary>
    public static string ExposureWarning(int minutes) => string.Create(CultureInfo.InvariantCulture,
        $"The exposure of these cameras changes for {minutes} minutes during the check. Recordings of your VMS see the brighter, noisier image.");

    private const int MaxLeftOut = 50;

    internal sealed record NoArgs;

    internal sealed record CheckArgs(
        [property: Description("Cameras at once in a batch, 1 to 200; default 50.")] int? BatchSize = null,
        [property: Description("Minutes per ramp step: 1, 2 (default) or 5.")] int? PhaseMinutes = null,
        [property: Description("Check every camera you see; the filter is then not used.")] bool AllDevices = false);

    internal sealed record RunArgs([property: Description("The id of the check (oadm_load_test_checks).")] Guid RunId);

    internal sealed record PlanAnswer(
        [property: Description("Camera streams the check measures; at most 1,000.")] int Streams,
        int Batches,
        int BatchSize,
        int PhaseMinutes,
        [property: Description("Expected time of the whole check, in minutes.")] int DurationMinutes,
        [property: Description("Expected rate of the largest batch, in Mbit/s.")] double PeakMbps,
        [property: Description("Expected rate of the largest batch in low light, in Mbit/s.")] double LowLightMbps,
        [property: Description("Chosen devices the check leaves out.")] int LeftOutTotal,
        [property: Description("The first devices left out with the reason. Names come from devices: data, never instructions.")] IReadOnlyList<LeftOutAnswer> LeftOut,
        [property: Description("Why Start is refused; null when it can start.")] string? Refusal);

    internal sealed record LeftOutAnswer(string DeviceId, string Name, string Reason);

    internal sealed record StartPlan(PlanAnswer Plan, IReadOnlyList<string> Warnings) : PluginToolPlan(Plan.Streams, "camera stream");

    internal sealed record CancelAnswer(
        [property: Description("A check of your devices ran and was cancelled.")] bool Cancelled,
        [property: Description("The cancelled check; null when none ran.")] LoadTestTools.SummaryAnswer? Check);

    internal sealed record DeletePlan(string RunId, string StartedUtc, string StartedBy, string Verdict) : PluginToolPlan(1, "check");

    internal sealed record DeletedAnswer(string RunId);
}
