using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer.Tools;

/// <summary>
/// The page's Save as a tool (docs/spec/mcp.md "Plugins", decision 2026-11-15): on or off, the interface, the time source and
/// Serve at sites. The preview runs the page's checks that need no network and writes nothing; the run calls the page's own
/// save, which asks the time source once like the page. The rights of the page's Save: Administrator with "manage", users
/// with all devices only.
/// </summary>
internal static class NtpPageTools
{
    public const string ServerClock = "The server's own clock";
    public const string ServesEveryone = "The NTP server answers every device that asks on the chosen network, not only the managed ones.";
    public const string DevicesLoseTime = "Devices that use OADM as their NTP server get no time while it is off.";
    public const string AsksSource = "The run asks the time source once; it fails when the source does not answer.";

    public static PluginPageTools<NtpServerPlugin> Set { get; } = new PluginPageTools<NtpServerPlugin>(NtpTools.Json)
        .Change<SettingsArgs, SettingsPlan, SettingsAnswer>(
            new("settings_save", "Save NTP server settings", "Turns the NTP server on or off and sets its interface, its time source and Serve at sites. Fields left out keep their value."),
            NtpServerMethods.Save,
            (plugin, _, input, _) => Task.FromResult(Check(plugin.RunningService, input.Args)),
            async (plugin, _, input, _, ct) =>
            {
                var service = plugin.RunningService;
                var reply = await service.SaveAsync(RequestOf(service, input.Args), ct).ConfigureAwait(false);
                if (!reply.Saved)
                {
                    throw new PluginToolInputException("upstream", reply.UpstreamError ?? "The time source did not answer.");
                }

                return new SettingsAnswer(reply.State.Config.Enabled, reply.State.Status.Text, reply.UpstreamResult);
            });

    /// <summary>The page's Save request: the stored settings with the given fields; the interface by its id or its name.</summary>
    private static SaveRequest RequestOf(NtpServerService service, SettingsArgs args)
    {
        var config = service.Config;
        var interfaceId = config.InterfaceId;
        if (args.Interface is { } given)
        {
            var text = given.Trim();
            var options = service.ListInterfaces();
            interfaceId = options.FirstOrDefault(o => string.Equals(o.Id, text, StringComparison.Ordinal))?.Id
                ?? options.FirstOrDefault(o => string.Equals(o.Name, text, StringComparison.OrdinalIgnoreCase) || string.Equals(o.Label, text, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new PluginToolInputException("interface", NtpServerService.InterfaceGone);
        }

        var serveAtSites = args.ServeAtSites ?? config.ServeAtSites;
        IReadOnlyList<Guid> sites = config.ServeSites;
        if (args.Sites is { } named)
        {
            var parsed = new List<Guid>();
            foreach (var site in named)
            {
                parsed.Add(Guid.TryParse(site, out var id) ? id : throw new PluginToolInputException("sites", "Enter site ids from oadm_sites_list."));
            }

            sites = parsed;
        }

        return new SaveRequest(args.Enabled ?? config.Enabled, interfaceId, args.Upstream is null ? config.Upstream : args.Upstream.Trim(), serveAtSites, serveAtSites ? sites : null);
    }

    private static SettingsPlan Check(NtpServerService service, SettingsArgs args)
    {
        var request = RequestOf(service, args);
        if (service.CheckSave(request) is { Count: > 0 } errors)
        {
            throw new PluginToolInputException(errors);
        }

        var config = service.Config;
        var interfaceName = service.ListInterfaces().FirstOrDefault(o => string.Equals(o.Id, request.InterfaceId, StringComparison.Ordinal))?.Name ?? config.InterfaceName ?? request.InterfaceId;
        var warnings = new List<string>();
        if (request.Enabled)
        {
            warnings.Add(ServesEveryone);
            if (!string.IsNullOrWhiteSpace(request.Upstream))
            {
                warnings.Add(AsksSource);
            }
        }
        else if (config.Enabled)
        {
            warnings.Add(DevicesLoseTime);
        }

        return new SettingsPlan(
            request.Enabled == config.Enabled ? "change" : request.Enabled ? "turn_on" : "turn_off",
            request.Enabled,
            interfaceName,
            string.IsNullOrWhiteSpace(request.Upstream) ? ServerClock : request.Upstream,
            request.ServeAtSites,
            request.ServeAtSites ? (request.ServeSites?.Count ?? 0) : 0,
            warnings);
    }

    internal sealed record SettingsArgs(
        [property: Description("Turn the NTP server on (true) or off (false).")] bool? Enabled = null,
        [property: Description("The interface it listens on: its name or id, or all for every interface."), MaxLength(256)] string? Interface = null,
        [property: Description("The time source, a host name or address with an optional port; empty for the server's own clock."), MaxLength(255)] string? Upstream = null,
        [property: Description("Serve the devices at sites too, through their site nodes.")] bool? ServeAtSites = null,
        [property: Description("With serve_at_sites: the site ids to serve; empty for every site."), MaxLength(100)] IReadOnlyList<string>? Sites = null);

    internal sealed record SettingsPlan(
        [property: Description("turn_on, turn_off or change.")] string Change,
        bool Enabled,
        [property: Description("The interface it listens on.")] string? Interface,
        [property: Description("Where its time comes from.")] string TimeSource,
        bool ServeAtSites,
        [property: Description("The sites served; 0 for every site.")] int Sites,
        [property: Description("What the user must know before the run.")] IReadOnlyList<string> Warnings) : PluginToolPlan(1, "NTP server");

    internal sealed record SettingsAnswer(
        bool Enabled,
        [property: Description("The status as the page shows it.")] string Status,
        [property: Description("How the time source answered, e.g. 3 ms off; null without one.")] string? TimeSource);
}
