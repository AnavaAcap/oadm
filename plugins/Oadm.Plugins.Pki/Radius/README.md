# 802.1X server (oadm.radius)

A PRO part of the PKI plugin (`../README.md`, `docs/spec/pki.md` "One plugin"). Spec: `docs/spec/radius-server.md` (with the
code map: "Code (protocol)", "Code (server)", "Code (page)"). License: `LICENSE` in this folder (source-available, not
Apache-2.0), also for `plugins/Oadm.Plugins.Pki.Client/Radius`.

- Core plugin `oadm.radius` "802.1X server" of the PKI package (group Security, icon `lock`). The core plugin always runs;
  the server (`RadiusService`) runs only while it is turned on (`ServerSwitch`, plugin setting `serverOn`, off by default)
  and OADM PRO is active; page methods `getServer`, `turnOn`, `turnOff` (`../Servers/ServerSwitch.cs`). OADM answers the
  switches as their RADIUS server and authenticates the cameras on the switch ports with EAP-TLS. Page methods and events:
  `Api/RadiusApi.cs` (shared with the page); events for the event rules "Device authenticated" and "Authentication failed".
- Server part (in `Oadm.Plugins.Pki.Server.dll`, namespace `Oadm.Plugins.Radius`):
  - `Server/`: `RadiusService` (listener with Listen on, a relay per site with switches, 64 workers, the tick of the
    Sessions table and the log), `SwitchTable` (immutable, swapped on every change), `DeviceCertificateValidator` and
    `CaTrust` (who passes), `DroppedRequests` (log rows of requests from a switch that is not added or with a wrong shared
    secret, at most one per sender and reason per minute), `DeviceIndex` (the managed devices in memory),
    `ServerIdentityKeeper` (the server certificate PKI's 802.1X server policy picks from the Certificates list, ECC, loaded
    with its key and again when the policies change; never renewed by itself), `SessionBoard`, `RadiusLog`,
    `RadiusPolicies` and `RadiusRuleSet` (the rules from PKI's policies: trusted CAs, `PolicyPlan`, the server policy's
    TLS, packet size and reauthentication; the default 802.1X policies), `RadiusStore`.
  - `Protocol/` (no sockets, no host, no UI): `Radius/` the own RADIUS codec (RFC 2865, 2869, 3579, 5080, 5997, 8044):
    packets with their attributes in order and raw, malformed packets as a result value, the Access-Request reader, the
    Response Authenticator and the Message-Authenticator (always the first attribute of an answer), the duplicate cache,
    MS-MPPE keys (RFC 2548) and VLAN attributes (RFC 3580); `Eap/` EAP packets (RFC 3748) and the EAP-TLS header with the L,
    M and S flags (RFC 5216); `Tls/` the BouncyCastle TLS server in non-blocking mode (the minimum version of the 802.1X
    server policy and the cipher suites that follow it, client certificate required and handed to
    `IClientCertificateValidator`, alerts in both directions), `TlsAlertTexts`, the cipher suite catalogue and
    `TlsServerIdentity`; `Sessions/` `RadiusProcessor` (one datagram in, at most one answer out; the session table,
    timeouts) and the session state machine with the spec's states; `Text/` MAC address normalizer, NAS-Port-Type names,
    text sanitizing.
- Settings: `config` (Listen on, Port, Log entries kept), `switches` (secrets encrypted with the master key, at least 8
  characters), `serverCertificate` (the certificate of before 4.5, key encrypted, read once to import it). Records: `log`,
  `logCertificates`. All in the encrypted backup.
- The Axis device ID CAs are shared with EST (`plugins/Shared/DeviceId`, linked with their PEM files).
- BouncyCastle (`BouncyCastle.Cryptography` 2.7.0) is compiled against and never copied into the plugin folder: the server
  ships the same version and the plugin load context falls back to it.
- Client part (in `Oadm.Plugins.Pki.Client.dll`, `plugins/Oadm.Plugins.Pki.Client/Radius`): the tab "802.1X server" of the PKI
  page (`RadiusPage.TabOf`); while the server does not run only the off state (`Servers/ServerOffStateView`: Turn on, Start
  30-day trial), else Server (Turn off left of Save, the link "N policies applied" with the policies as tooltip, opens the
  Policies tab) above Switches (row menu, the switch dialog) left and one Sessions table right (in progress on top, then the
  log: `AuthenticationsViewModel`; search, View certificate, Delete all), one column on narrow pages
  (`Servers/ServerPageLayout`). The page uses the server part's contract `Api/RadiusApi.cs` (the client part references the
  server assembly).
- Tests: `tests/Oadm.Plugins.Radius.Tests` (a BouncyCastle client as the camera through an in-memory switch, RFC vectors,
  parsing rules, fuzzing, fragmentation, alerts, timeouts; the plugin end to end through its in-memory UDP listener and site
  relays, `ServerTests`; `HardeningTests`; the device-group canary and filtering, `PluginTests`; PKI's policies,
  `AllowCaTests`, `PolicyTests`, `ConversionTests` (the default policies, added once); `Category=Perf` for many parallel
  sessions and 1,000 authentications through the plugin among managed devices at the device limit). Client: `tests/Oadm.Plugins.Radius.Tests/Client` against
  `FakeRadiusBackend` (view models, headless rendering and screenshots with `OADM_SCREENSHOT_DIR`, `ServerLayoutTests`,
  `PkiTabHeadlessTests`, `Category=Perf` for 10,000 sessions and the device limit).
- Not built yet: recorded conversations of real switches (Cisco, Aruba, ...): none could be recorded here; the tests replay
  the RFC examples. A new switch quirk becomes a fixture first.

## MCP

The read-only tool `oadm_radius_log` (`Tools/RadiusTools.cs`, docs/spec/mcp.md "Plugins"): the state and the log of the
caller's devices, newest first, paged, with a result filter on top of the log's own search; never a shared secret or a
certificate; while the server is off its state only. Tests: `ToolTests`.

The page's actions (`Tools/RadiusPageTools.cs`, `PluginPageTools`, decision 2026-11-15), each with the rights of its page
method: `oadm_radius_sessions` (the Sessions tab of the caller's devices, newest first, paged; no sessions while the server is
off) and `oadm_radius_log_delete_preview` / `_run` (Delete log: Administrator, "manage", users with all devices, the run
audited; an empty log is refused, "The log is empty."). Tests: `PageToolTests`.

The server's actions (decision 2026-11-15, same file): `oadm_radius_switches` (read: id, name, address, site, state; never a
secret), `oadm_radius_enable` (Turn on with OADM PRO, Turn off; audited as `turnOn` / `turnOff`), `oadm_radius_settings_save`
(Listen on, port, log lines kept; the checks of `SaveSettingsAsync` in `RadiusService.SettingsRefusal`), `oadm_radius_switch_save`
and `oadm_radius_switch_delete`. A switch's shared secret never passes through an answer: `secret` is `keep` (a change's
default), `enter` (the slot `shared_secret` of the host's secret channel, `PluginTool.Secrets`; the plan lists it in
`needs_secrets`) or `generate` (the run makes one with `GenerateSecret` and hands it out once as a text file:
`Pki/Tools/ToolFileStore`, download `toolFile`, bound to the caller's session, 10 minutes). The preview checks with a
stand-in secret; the run checks the real one ("secrets.shared_secret"). Every change Administrator, "manage", users with all
devices, the run audited with its page method (`AuditDetail`). Tests: `PageToolTests`.
