# VPN Fleet

Core plugin `oadm.vpn-fleet` (+ client part), rail page **VPN Fleet** (group Security, icon `globeLock`), free, **off by
default**. Community plugin by [Mo3he](https://github.com/Mo3he) for the camera VPN apps of github.com/Mo3he: Tailscale,
NetBird, ZeroTier, WireGuard, OpenVPN and WireGuard Server. Code: `plugins/Oadm.Plugins.VpnFleet(.Client)`, its README has
the decisions and the device API.

## User story

As an installer who reaches cameras through a VPN app on the camera, I see which cameras are connected, change the
settings of one camera's app, set up many cameras at once with keys that differ per camera, and install or upgrade the
apps from their GitHub releases. The Applications page cannot do this: its Change settings sends the same values to
every device, and a WireGuard camera needs its own key pair and tunnel address.

## Page

- Devices: the page's device choice (`PageDeviceChoice`); nothing is read before one. Refresh reads the chosen devices in a
  job of the caller, 16 at a time, 15 s each: `applications/list.cgi`, then the status of each VPN app that runs (the
  app's `status.json`, NetBird `api/status`, WireGuard Server `status`, WireGuard the app's lines of
  `systemlog.cgi`). One row per VPN app and device, or one row "No VPN app" or with why the device was not read.
  One table per device scope.
- A row opens the camera's panel: the app's parameters (param.cgi of the app's group) with labels, Save for this camera
  only, the version and **Upgrade to x.y.z**, **What's new** (the GitHub release page, nothing else is opened). A camera
  without a VPN app offers to install one.
- **Install or upgrade** and **Set up many cameras** open a wizard (`ui:WizardSteps`, like Configure devices): 1 the app
  and its newest version on GitHub or packages on this computer (below), or the VPN and its settings; 2 the chosen cameras in a table with a tick each, the installed
  version and what the action does to the camera ("Upgrade 1.104.6 to 1.104.7", "App not installed"), a search, and
  ticks for all shown, none, older version / without the app / with the app; only cameras where it makes sense start
  ticked, the WireGuard hub can never be ticked, ticks stay when the user goes back unless the app, hub or version
  changed; 3 what happens to how many cameras, a warning for ticked cameras whose tasks would fail, and Start, which
  names the count. The wizard closes once the tasks started; a refused start stays open with the reason. The set up
  form is new in every wizard and its keys are cleared once sent. One camera's Upgrade and Install stay in its panel.
- **Packages on this computer** (the maintainer's review: OADM without internet): the user picks the `.eap` files of one
  release (at most 8, `LocalPackages`). The page reads them with the `.eap` reader of Applications (ACAP) and refuses
  another app or several versions before anything is uploaded; the cameras step marks cameras no file fits (the same
  choice as below, on the cached architecture and AXIS OS) and does not tick them. Start uploads the files
  (`UploadAsync`, the caller's session) and sends their ids; the server reads them again from the caller's uploads and
  refuses the same problems and a gone upload before a task starts. The task reads the uploads (`ctx.Files`), skips
  Download ("Uploaded from your computer") and installs the build `AcapBuildChooser` picks for the device. GitHub is not
  asked. The tools stay GitHub only.
- **WireGuard peers**: the `[Peer]` sections of the cameras set up for another WireGuard server.

## Rules that must hold

- **Secrets** (auth and setup keys, private keys, passwords) never reach the client and are never stored, logged or
  audited: a settings read answers whether a secret is set, never its value; a secret sent empty keeps its value. Task
  payloads carry them in memory only. The plugin's setting `wireguard` keeps per camera the peer name, public key,
  address, network and hub, never a private key.
- **Device safety**: a change writes only parameters the device has, checked before the first write (one line, the
  values of on/off and choices, ports, keys, the ZeroTier network ID, addresses); one `param.cgi` update per save. Set up
  refuses a device without the app ("Nothing was changed"). Install chooses the package per device (architecture and
  AXIS OS from `basicdeviceinfo`): the signed build, on armv7hf with AXIS OS before 10.12 the ACAP 3 build, never a
  `_root` build; uploaded packages: the build `AcapBuildChooser` (Applications' rules: architecture, AXIS OS of the
  package's schema) picks and that fits the device, else "Nothing was changed"; a device that has the release's
  version is not changed; upgrades keep the app's settings.
- **GitHub** (the only HTTP of the plugin outside `IVapixClient`, `GitHubHttp.cs`, listed in
  `RoutingRulesTests.AllowedBannedApiUsers`): HTTPS to `api.github.com` for `repos/Mo3he/<repository>/releases/latest`
  (kept 30 minutes, a failure 5 minutes, so the 60 requests per hour without a login are never used up) and to
  `github.com` / `*.githubusercontent.com` for the packages. No cookies; redirects are followed by hand and only to those
  hosts; only `.eap` files below the app's own `releases/download/` count; at most 200 MB; the SHA-256 GitHub publishes
  is checked before a package is used. Packages are kept in the plugin's data folder (`packages/`). The server needs
  outbound HTTPS to those hosts; without it the page says why and the rest works.
- **WireGuard**: key pairs are made on the server (X25519, RFC 7748). With a camera running WireGuard Server as hub the
  plugin reads the hub's key, network, port and public address, gives each camera the lowest free address (its earlier
  one when it is still free; the hub's other peers keep theirs) and adds the cameras to the hub's `Peers` (records it
  cannot read are kept). One WireGuard run at a time. A new key saved on a camera behind a hub goes to the hub.
- Tasks: `oadm.vpn-fleet.connect` (save the settings, start the app, wait up to 90 s for the tunnel, a warning when it
  does not come up) and `oadm.vpn-fleet.install` (at most 4 devices at a time), started from the page only.

## Permissions

- Show (automatic): the page, reads and settings without secrets.
- `connect` "Change VPN settings": save a camera's settings, set up many cameras. Off for Operator.
- `install` "Install and upgrade VPN apps". Off for Operator.

Saves, set ups and installs are audited (app, setting names, device counts; never a value). Users with a device group
reach only their devices (`PluginCanary` for every method and tool).

## MCP

Tools with the page's rights ([mcp.md](mcp.md) "Page actions"): `results` (paged), `read`, `releases`, `settings`
(never a secret value), `install` and `settings_save` as preview and run. `settings_save` refuses secrets ("change
secrets on the VPN Fleet page"); setting up many cameras with keys has no tool.
