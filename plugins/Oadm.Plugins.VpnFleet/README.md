# VPN Fleet (`oadm.vpn-fleet`)

Community core plugin (+ `.Client`) by [Mo3he](https://github.com/Mo3he), off by default: the rail page **VPN Fleet**
(group Security, icon `globeLock`) for the camera VPN apps of github.com/Mo3he. Spec: [vpn-fleet.md](../../docs/spec/vpn-fleet.md),
[Bundled plugins](../../docs/spec/bundled-plugins.md#vpn-fleet-core-plugin). Questions and feature requests:
https://github.com/Mo3he.

| App | Package (`appName`) | Repository | Status read from | Set up writes |
|---|---|---|---|---|
| Tailscale | `Tailscale_VPN` | Axis_Cam_Tailscale | `/local/Tailscale_VPN/status.json` (`tailscale status --json`) | `AuthKey`, optional `CustomServer` |
| NetBird | `NetBird_VPN` | Axis_Cam_NetBird | `/local/NetBird_VPN/api/status` (a proxy error page: not running) | `SetupKey`, optional `ManagementURL` |
| ZeroTier | `ZeroTier_VPN` | Axis_Cam_ZeroTier | `/local/ZeroTier_VPN/status.json` | `NetworkID` |
| WireGuard | `wireguardconfig` | Axis_Cam_WireGuard | the app's lines of `/axis-cgi/admin/systemlog.cgi` (no status file; a handshake proves the tunnel) | `PrivateKey`, `ClientIP`, `Endpoint`, `PeerPublicKey`, `AllowedIPs` |
| OpenVPN | `OpenVPN_VPN` | Axis_Cam_OpenVPN | `/local/OpenVPN_VPN/status.json` | the profile (`POST /local/OpenVPN_VPN/api/profile`), optional `Username`, `Password` |
| WireGuard Server | `WireGuard_Server` | Axis_Cam_WireGuard_Server | `/local/WireGuard_Server/status` | `Peers` (hub only, "name,publicKey,address" records, `;` between) |

## Parts

| Part | Contents |
|---|---|
| `VpnFleetPlugin` | Page methods (`Fleet`: `getState`, `refresh`, `connect`, `getPeers`, `getReleases`, `install`, `getSettings`, `saveSettings`), the table per device scope, WireGuard key and address planning, the `wireguard` setting (public keys only) |
| `ConnectTaskPlugin` (`oadm.vpn-fleet.connect`) | Steps: Check the device, Upload the VPN profile (OpenVPN), Save the VPN settings, Start the app, Wait for the VPN to connect (90 s, a warning when it does not). The hub run: Read the peers of the WireGuard server, Add the cameras as peers. Names: "Connect to Tailscale", "Add 3 cameras to the WireGuard server" |
| `InstallTaskPlugin` (`oadm.vpn-fleet.install`) | Steps: Check the device, Download the package, Install the package, Start the app, Check the installed version; 4 devices at a time. Names: "Install Tailscale 1.104.7", "Upgrade Tailscale to 1.104.7" |
| `LocalPackages` | Installs without internet: the uploaded `.eap` files of one release, read and checked with the shared Applications (ACAP) code (`..\Oadm.Plugins.Acap\Shared`, linked) |
| `ReleaseCatalog`, `GitHubHttp` | The newest release of each app (`api.github.com`, 30 minutes), the package per device, downloads checked against GitHub's SHA-256; `GitHubHttp.cs` is the plugin's only banned-API user |
| `DeviceVpn`, `VpnStatusParser` | Every device request (`IVapixClient`): app list, start, `param.cgi` update (values in the body), `upload.cgi` (field `file`, quoted names), the status per app |
| `AppSettings`, `ConnectRules` | The settings of each app with labels and checks; the rules of a set up (also the page's) |
| `WireGuardKeys`, `WireGuardAddressing` | X25519 (RFC 7748 test vectors in the tests), tunnel addresses, the hub's peer list |
| `Tools/VpnFleetTools` | The page as tools (`PluginPageTools`): `results`, `read`, `releases`, `settings`, `install`, `settings_save` |
| `.Client` | `VpnFleetViewModel` (table, camera and peers panels), `CameraSettingsViewModel` (one camera), `RolloutWizardViewModel` + `RolloutWizardWindow` (the wizards of Install or upgrade and Set up many cameras), `InstallViewModel` and `ConnectFormViewModel` (their first steps), `CameraChecklist` (the cameras step: search, ticks, counts) |

## Decisions

- One row per app and device: a camera can run two VPN apps. The WireGuard Server rows show the hub and its connected
  peers.
- Events carry only the changed devices (`rows`, at most 200 devices) and the read state (`state`); `getState` comes in
  pages of 5,000 devices; the page applies the rows in place by version per device.
- Secrets never reach the client: a settings read sends whether a secret is set; an empty secret field keeps the value.
  The client sends only the fields the user changed.
- Install picks the signed build of the device's architecture; on armv7hf with AXIS OS before 10.12 the ACAP 3 build
  (the apps' READMEs: ACAP 3 for AXIS OS 9 and 10); never the `_root` builds (AXIS OS 12 removed root).
- Without internet, Install or upgrade takes the `.eap` files of one release from the user's computer: uploaded at the
  start, each device gets the build `AcapBuildChooser` picks (the same choice as Applications), GitHub is not asked.
- Actions on many cameras are wizards with the cameras in a full table (a list in the side panel does not scale past a
  few dozen cameras). They tick only the cameras where the action makes sense (the app installed for a set up, an older
  version for an upgrade); the user ticks others on purpose, and the last step warns about ticked cameras whose tasks
  would fail.
- New key pair on the panel: the server makes the key pair and writes the private key to that camera only (`newKeyPair`,
  the checks of a save); the page gets the public key. A camera behind a hub that gets a new private key: the plugin
  updates its record and starts the hub's peer task with the new public key.
- Not in fake mode.

## Device API and verification

Read: `applications/list.cgi`, `basicdeviceinfo` (architecture, AXIS OS), `param.cgi?action=list&group=<app>`, the
status of each app (table above). Write: `param.cgi` update (POST body), `applications/control.cgi?action=start`,
`applications/upload.cgi`, the OpenVPN profile. Verified against the apps' sources (versions in the table of the spec)
and GitHub's live releases (asset names, digests, redirect to `release-assets.githubusercontent.com`). Not yet verified
on a range of real cameras: report a camera where it fails with its model and AXIS OS.

## Tests

`tests/Oadm.Plugins.VpnFleet.Tests` on the fakes of `Oadm.Sdk.Testing` with a fake GitHub (`FakeGitHub`): status parsers
of every app, settings and their checks, X25519 against RFC 7748, address planning, the tasks step by step, install
(package choice, digest, redirects that leave GitHub), the tools (previews never write, secrets refused), the
`PluginCanary` for every method and tool, the wizards step by step, the page and both wizards rendered headless (`OADM_SCREENSHOT_DIR`) and 100,000 devices.
