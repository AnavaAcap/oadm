using System.Text.RegularExpressions;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// What a request's path tells of itself, for the request builder and the agents' device request: the API it needs
/// (param.cgi: param-cgi 1.0, <c>/config/rest/&lt;api&gt;/v&lt;n&gt;</c>: &lt;api&gt; n.0, the known CGIs) and the kind of its answer.
/// </summary>
internal static partial class RequestDefaults
{
    private static readonly Dictionary<string, (string Api, string Version, string Kind)> KnownCgis = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/axis-cgi/param.cgi"] = ("param-cgi", "1.0", ResponseKinds.ParamCgi),
        ["/axis-cgi/basicdeviceinfo.cgi"] = ("basic-device-info", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/apidiscovery.cgi"] = ("api-discovery", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/network_settings.cgi"] = ("network-settings", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/pwdgrp.cgi"] = ("user-management", "1.0", ResponseKinds.Text),
        ["/axis-cgi/firmwaremanagement.cgi"] = ("fwmgr", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/time.cgi"] = ("time-service", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/ntp.cgi"] = ("ntp", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/mqtt/client.cgi"] = ("mqtt-client", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/lightcontrol.cgi"] = ("light-control", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/io/portmanagement.cgi"] = ("io-port-management", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/daynight.cgi"] = ("daynight", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/systemready.cgi"] = ("systemready", "1.0", ResponseKinds.JsonRpc),
        ["/axis-cgi/applications/list.cgi"] = ("application", "1.0", ResponseKinds.Xml),
        ["/axis-cgi/applications/control.cgi"] = ("application", "1.0", ResponseKinds.Text),
        ["/axis-cgi/restart.cgi"] = ("param-cgi", "1.0", ResponseKinds.Text),
        ["/axis-cgi/siren_and_light.cgi"] = ("siren-and-light", "1.0", ResponseKinds.JsonRpc),
    };

    /// <summary>The API and the answer's kind a path is known for; null for a path without a rule.</summary>
    public static (string Api, string Version, string Kind)? Of(string path)
    {
        if (KnownCgis.TryGetValue(path, out var hit))
        {
            return hit;
        }

        return RestPath().Match(path) is { Success: true } rest
            ? (rest.Groups[1].Value.ToLowerInvariant(), rest.Groups[2].Value + ".0", ResponseKinds.Rest)
            : null;
    }

    /// <summary>The kind of the answer of a path without a rule: XML for <c>/vapix/</c>, else text.</summary>
    public static string KindOf(string path) =>
        Of(path)?.Kind ?? (path.StartsWith("/vapix/", StringComparison.OrdinalIgnoreCase) ? ResponseKinds.Xml : ResponseKinds.Text);

    [GeneratedRegex("^/config/rest/([a-z0-9-]+)/v([0-9]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RestPath();
}
