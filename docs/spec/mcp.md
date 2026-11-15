# MCP server

Status: **all four stages built** (stage 1: the endpoint, the card MCP of the rail page MCP & Assistant, the PKI policy MCP
server, access keys and the read tools; stage 2: the SDK members for tasks, `ValidatePayload` in `TaskService.Run`, the task catalog,
preview and run, the schemas of the bundled tasks without secrets, cancel, file links; stage 3: device actions, tags,
sites, compliance, event rules, plugin tools; stage 4: the skill, Connect an agent, the website pages; see the "as built"
sections). Not offered on purpose: see "Open points".

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

An AI agent (Claude Code, VS Code, any MCP client) can do through MCP what a user can do in the client, with the role,
device group, checks and audit log of its access key. "MCP" is the name of this feature in the UI (the rail page MCP & Assistant, card
MCP; the PKI policy MCP server). The MCP server is part of the OADM server (core, free,
Apache-2.0), off by default. Plugins add their own tools through the SDK, so a new plugin is usable by agents without a
host change.

## Rules that never break

1. **The agent is a client.** Every tool calls the server's gRPC services in process with the access key's caller
   (`InProcessAccess`, `CallerContext`): `AccessPolicy`, device groups (`DeviceView`), validation, limits and the audit log
   apply exactly as for the client. A tool never reads stores, `VapixClient` or plugins around that layer.
2. **No change without a preview.** Every tool that changes something (devices, tags, rules, files, tasks) is two calls:
   a preview that checks all input on the server and returns the plan and a handle, and a run that takes only that
   handle. Nothing is written by a preview. One exception: `oadm_files_upload_link` hands out a one-time link and writes
   nothing itself; the file sent to it is the change, and a task that uses the file is again a preview and a run
   (`ToolRegistryTests.PreviewAndRunStayTwoTools` names it as the only exception).
3. **Never all devices in a result.** Lists return at most 50 rows by default (at most 200), always with the total and a
   cursor; the first answer about devices is a summary (counts), never a list. Logs are the exception: a log list answers
   `more` and a cursor instead of a total (a text or time filter would need a count of the whole log).
4. **No secrets.** No tool takes or returns a password, private key, license key, access key or token. Stored
   credentials are used by name; a task that needs a new password is not offered over MCP ("Set the password in OADM.").
5. **Least rights.** The tool list shows only what the key may do now (its role and device group, plugins on); a role like
   Viewer gives a look-only agent; a role that may run no task (no `task:` right) lists no task or file tool and is refused
   "Your role may run no task." (`TaskRunTools.NoTaskRight`). An agent can never raise rights: users, roles, device groups, access keys, license, plugins on/off, server
   settings, backups (create, download, restore) and data resets are GUI only.
6. **Device data is data.** Texts written by devices or other users (names, locations, tags, event texts, log lines) are
   returned only as JSON field values, never inside tool descriptions or messages to the agent, and the skill says they are
   never instructions.

## Tools and MCP

The tools do not depend on MCP: the built-in assistant (`docs/spec/assistant.md`, a chat panel of the client that runs on
the server against a hosted or local model) calls the same tools in process, with the same `oadm_*` names, schemas, device
filter, preview/run handles and caller rules.

- **One tool registry** in the server (`src/Oadm.Server/Tools/`, `ToolRegistry`): every tool's descriptor (name, title,
  description, read only or change, starts tasks), its input and output schemas, its permission (the gRPC methods it calls,
  `AccessPolicy`) and its invoke method. It knows nothing of MCP: no MCP SDK and no ASP.NET request types (an architecture
  test checks the source and the compiled types).
- **The caller is a parameter.** A tool gets a `ToolCaller`: the caller with the role and devices of now (for MCP the
  access key itself), the key cursors and handles are bound to, and what the channel tells of itself (its access key and
  its own limits, for `oadm_server_info`). Nothing comes from the HTTP request; an in-process gRPC call carries only the
  caller's address. The registry checks only each tool's own rules (`AccessPolicy` of its methods, its gate); a channel adds
  no permission of its own (the assistant's chat needs "Use the assistant" on its gRPC methods, never on a tool).
- **MCP is a thin adapter** (`src/Oadm.Server/Mcp/`): the transport and its checks, access keys (which make the
  `ToolCaller`), its own limits (calls per second, calls at once, changes per minute, per key and for the port), the mapping to `tools/list` and
  `tools/call` (`McpToolAdapter`, also an image result as MCP image content), the file links and the skill resources.
- **Preview and run stay two tools.** A change is always a preview tool `<name>_preview` (it writes nothing and answers the
  plan and a handle) and a run tool `<name>_run` that takes only the handle: the assistant shows the preview as a card with
  a Run button. A handle is taken only by its own run tool. `ToolRegistryTests` checks all three lines: the tool layer's
  source and types name no MCP or ASP.NET, two tools run in process with a caller object, and every change is such a pair.

## Transport and certificate

- Streamable HTTP, MCP revision 2026-07-28 (stateless: no session id, no `initialize`), one endpoint `POST /mcp` on its own
  port (default **TCP 5082**), Kestrel in the server process, official C# SDK `ModelContextProtocol.AspNetCore` 2.x
  (`WithHttpTransport` stateless, `MapMcp`). No stdio, no SSE GET stream, no sampling, no MCP logging (deprecated).
- **Off by default.** Rail page **MCP & Assistant** (above Permissions, every user; administrators change it), card **MCP** (GUI only; the card Assistant stands right of it): Turn
  on, Port, Listen on (the interfaces that are up with an address other than IPv6 link-local, and the loopback;
  `McpListener.Interfaces`), the certificate, **Connect an agent** (see below), **Download
  skill**, **Access keys** (administrators). Off: the port is closed.
- **Certificate**: picked from the PKI Certificates list by the PKI server policy **MCP server**, like the EST and 802.1X
  servers (`docs/spec/pki.md` "Server certificates": a certificate with its key, server authentication, not ended, not on
  the not-accepted list; the policy refuses a certificate that does not name the server's addresses or host name). Its own
  certificate, never the shared "802.1X and EST server" (decision 2026-11-15): the first Turn on, while the policy has none,
  makes "MCP Server Certificate" (this computer's addresses and host name, plus `localhost`, `127.0.0.1` and `::1` for an
  agent on the server's computer, `PkiService.LoopbackNames`; the CA of the shared one, ECC P-256, server
  authentication), also without OADM PRO; the policy's Certificate field changes it afterwards. PKI never makes or assigns
  one for this policy at a start or a license change. The agent's computer trusts its CA once (**Download CA certificate** on the card; Claude Code: `NODE_EXTRA_CA_CERTS`, else
  the OS store). Without PKI running, the card says "PKI is not running. The server log says why." and MCP cannot be turned on.
- Origin check (403 for a browser origin that is not the server), `MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name`
  checked against the body (-32020), request body at most 1 MB (files go through upload links), answers at most 32 KB and
  not compressed.
- Windows service: the firewall rule for the port exists only while MCP is on (`FirewallRuleKeeper`,
  `docs/spec/security.md` "Service: firewall and folders"). Site nodes do not relay MCP.

## Access keys

Decision 2026-11-15 (redesign): a key is an identity of its own, made by an administrator, never a login of the user who
made it. There is no Read only flag and no permission "Use AI agents": the key's role decides what an agent may do.

- **Administrators only**, on the page **MCP & Assistant**, card **MCP**, part **Access keys** (`AccessKeyService`, every
  method `AccessRule.Admin`). **Create access key** opens a dialog (`Settings/AccessKeyFormWindow`): Name (1..64 letters,
  digits, spaces, `-`, `_` and `.`, unique on the server, case-insensitive), Role (any role of the role list), Devices
  (All devices or one device group; an Administrator key always has all devices, the field is disabled with "Administrators
  always have all devices."), Expires (30, 90 or 365 days, default 90). Errors show below their fields; the server's refusals
  name their field (trailer `oadm-field`: name, role, devices, days). After Create the dialog shows the key once with Copy
  ("Copy it now. OADM cannot show it again."); Cancel becomes Close. At most 100 keys ("Use at most 100 access keys.
  Delete one first.").
- The list: Name, Role, Devices, Created, Expires, Last used and Delete (a red icon button; it asks "Delete access key",
  "Agents that use <name> can no longer connect.", the red Delete). Other roles see no access keys.
- **Stored** in the table AccessKeys (migration `AgentAccessKeys`): its id, the SHA-256 of the key (32 random bytes,
  base64url, prefix `oadm_`), name, role, all devices or the device group, who made it (for the audit only), created,
  expires, last used (written at most once a minute). Never a session: a session token is no key and a key is no session.
- **Who calls**: the key itself. Its calls run with the key's role and devices as stored at each call, so a role change or
  new group rules apply to the next call. The caller's user name, the audit user and the task owner read "MCP <key name>"
  (for example "MCP Lobby-bot", `AccessKeyStore.AgentNameOf`); its user id is the key's id (handles, limits, queued tasks:
  `DeviceViewFactory.ForUserAsync` finds the key's devices when no user has that id).
- **What ends a key**: Delete (at once, its jobs end too), its end date (the hourly cleanup removes the row; until then a
  refusal names the key), a restore (backups hold no keys). A role or device group a key names cannot be deleted: the
  refusal names the keys ("The access key Lobby-bot has the role Agents. Delete the key first.", the same for a device
  group), like a role that users have.
- The agent sends `Authorization: Bearer <key>`. Missing or ended key: HTTP 401 with "The access key is not valid. An
  administrator creates a new one in OADM on the page MCP & Assistant."; not allowed: the tool is not listed, and a call answers
  `isError` with `AccessPolicy.DeniedMessage`.
- Audit: every change, upload and download an agent causes as "MCP <key name>"; "Created access key" (the name; role,
  devices and the end in the detail) and "Deleted access key" by the administrator who did it; every failed key as "Access
  key refused".
- Limits (HTTP 429 with Retry-After), per key: at most 20 calls a second, at most 4 calls waiting at once and at most 10
  changes a minute ("Too many changes with this access key. Wait a minute."); the whole port takes at most 32 calls waiting
  at once. Every tool that is not a plain read counts as a change (`OadmTool.CountsAsChange`: a run, a task start, or a call
  that acts outside OADM such as the add preview). A call is counted as soon as its key is valid, before the role and device
  view are read. Open handles and links: at most 20 of a kind, 60 in all and 16 MB per key, the oldest go first. The skill
  needs no permission (any valid key). Camera pictures have their own limit, kept by the server for every channel
  (`LiveViewService.GetSnapshot`): 30 a minute per caller (a user's sessions together, each key alone); MCP answers 429 "Too
  many pictures with this access key. Wait a minute." with Retry-After 60. One `oadm_devices_snapshot` call can take up
  to three pictures (a smaller size when the first is over 1 MB). File transfers (PUT and GET on `/files/`) have their own
  places, 2 at once per key and 8 on the port, and never take the 32 places of `/mcp`; a transfer must move at least 1 KB a
  second after 10 seconds, and end within 2 minutes plus its size at 100 KB/s (15 minutes when the size is not known), or
  it ends ("The transfer took too long and was ended.", 408) and its place is freed. Changes and pictures are counted from
  the call's own tool name, not from a header; a call the tool list refuses (the role) and an unknown tool count against no
  limit. `oadm_server_info` names the change limit `changes_per_minute`. The MCP host logs ASP.NET requests at Warning
  only, so no upload token reaches the server log.


## Tools

One tool per user action, not per gRPC call, named `oadm_<area>_<action>` (`[A-Za-z0-9_]`, at most 64 characters).
Every tool has a title, a description of one to three sentences (what it is for, what to call before or after), a strict
`inputSchema` (JSON Schema 2020-12, `additionalProperties: false`, enums and formats such as `ipv4`) and an
`outputSchema` with `structuredContent` plus the same JSON as text. Annotations are honest: reads `readOnlyHint`; previews
read only; runs `destructiveHint` true unless the action only adds (tag, rule) and `idempotentHint` where it holds;
`openWorldHint` false (OADM's own devices).

### Devices and the device filter

Every tool that works on devices takes one **device filter**, resolved on the server inside the caller's device group:

```json
{ "ids": [], "search": "lobby", "tags": ["Lobby"], "sites": ["Parking"], "models": ["M3085-V"],
  "firmware": { "below": "12.11" }, "status": ["Ok", "Unreachable"], "categories": ["Camera"] }
```

All fields optional and combined with AND; an empty filter means every device the caller sees: a list of them comes page by
page, every page with the total and a cursor. `"selection": true` means the devices the user selected in the client's
device grid (`ToolCaller.Selection`, sent by the assistant with every message and cut to the caller's view; with `ids`
as well, the ids that are both). The registry applies it after the schema check (`SelectionTools.Apply`); nothing
selected is a refusal ("No devices are selected. Select them in the device list first."), never every device. Over MCP
nothing is selected.

- `oadm_server_info`: version, who calls ("MCP <key name>"), role, the permissions, the limits, the device count. The first call.
- `oadm_devices_summary(filter, group_by)`: counts by status, model, AXIS OS version, site, tag or category (at most 50 groups,
  the rest as "Other").
- `oadm_devices_search(filter, fields, sort, cursor, limit)`: rows with id, address, host name, UPnP name, model, AXIS OS
  version, status and the requested fields; `total`, `next_cursor`. A device has no one name: its address, host name and
  UPnP name are separate fields and the search matches each. The cursor is the last row's sort key and id, bound to the key
  and the search, so a device added, removed or changed elsewhere never moves the rows between pages. Truncated answers say
  how to narrow ("312 more. Add a filter, e.g. site or model.").
- `oadm_selection(fields, cursor, limit)`: the selected devices as pages of `oadm_devices_search` rows; over MCP an empty
  page with a note.
- `oadm_devices_get(device_id)`: one device with its details (the client's device details, no credentials).
- `oadm_devices_snapshot(device_id)`: one JPEG of the camera as image content (Live view permission, at most 1280 px wide).
- Refresh, Remove, Set credentials (by stored credential name), Add devices (Network range, Add manually, the waiting
  list; logins with the credential list; only addresses inside a network of the server or of a connected site, at most
  200 devices a run) as preview/run pairs.

### Tasks

- `oadm_tasks_catalog(filter)`: the tasks the caller may run with, per task, how many of the filtered devices can run it
  and the reasons of the others summed up (`NotSupportedReason`, as the context menu), and the task's input schema.
- `oadm_task_preview(task, filter, input)`: checks the input (the plugin's `ValidatePayload`, see "SDK"), resolves the
  filter to a fixed device list and answers the plan: `will_run` count with the first 20 devices, `skipped` grouped by
  reason ("No file for this model.", "Already up to date", "Needs AXIS OS 11.11 or later"), the task name
  (`GetTaskName`), warnings (the dialog's warnings, e.g. a network change that can make devices unreachable), and a
  **handle** (10 minutes, once, bound to the key; the input stays in memory only, like every payload). Invalid input:
  `isError` with one entry per field (`{"field": "ip", "error": "Enter an IPv4 address."}`, the dialog's texts).
- `oadm_task_run(handle)`: the run; re-checks the permissions and the role's `RequiredPermissions`; answers the batch id
  and the task count. Each task still re-checks its device (`Require`, HARD RULE device safety).
- `oadm_tasks_list(batch, status, cursor)`, `oadm_tasks_get(task_id)` (steps and per-task log), `oadm_tasks_cancel`
  (preview/run). Waiting for tasks: the agent polls `oadm_tasks_list(batch)`, which answers counts per state first.

### Other areas

Each a few tools with the same rules (summary or search first, preview/run for changes): tags, sites (read), compliance
(results, checks), event rules (the catalog of events and actions `oadm_rules_catalog`, list, get, create, change,
delete, the event log), logs (server log search with `since`, `level`, `text`, at most 200 lines; audit log), files
(`oadm_files_upload_link`, list, delete), settings (read only), PKI policies (read only, `oadm_pki_policies`, a tool of
the PKI plugin), the user docs (`oadm_docs_search(query)`: at most 5 pages of oadm.app with title, address and the best
passage, from the index the server is built with, `Assistant/DocsIndex`; no gRPC method, so every caller has it).

### Plugins

Core plugins and task plugins bring their tools through the SDK; the host prefixes them `oadm_<plugin area>_` and lists
them only while the plugin is on and the role has its permission. Bundled plugins get tools for their pages (e.g. PKI
certificate list and Create certificate, NTP and DHCP status and leases, Hardening scan results, VAPIX Commander commands
and their run). A task plugin without a payload schema is not offered over MCP (fail closed).

**VAPIX Commander** (`oadm_vapix_commands`, `oadm_vapix_command_preview` / `_run`, `CommanderTools`): only library and saved
commands by id (`library:<id>`, `saved:<id>`), never a request of the agent's own. Not offered: a dangerous command
(`RequestSafety`, of the definition and again of the filled-in request), one with a password or a file field, and one that
changes what OADM's own tasks change (date and time, NTP, applications, AXIS OS, restart, users, network; their reads stay).
Every command, a read too, goes preview then run. The preview checks the values (the page's renderer) and plans the chosen
devices from cached data (an id outside the caller's view refuses the call; Certificate changed, Site offline and a missing
API skip a device); the run is the page's rollout, one task per device, with the user's OK of the preview as the
confirmation, and needs the plugin's "Run on many devices" right. At most 1,000 devices a run. An agent's run masks secret
values as `***` in its steps and log (`RolloutPayload.MaskSecrets`, `SecretValues`): a value whose label, parameter or path
names a secret (community, password, key, token, ...), never a yes/no value.

### Not over MCP

Live video, Live View tabs, Dashboards live values, Metadata Monitor and I/O Monitor streams, Web View; users, roles,
device groups, access keys, license, plugins on/off, Forget server; Settings changes; backup restore, Delete all tasks,
Delete all devices. Backups stay GUI only: no tool creates, downloads or restores one (decision 2026-11-15).

## Files

`oadm_files_upload_link(name, size)` answers a one-time HTTPS URL on the MCP port (10 minutes, one file, the file limits
of `FileService`, PUT with the same Bearer key) and the command for the agent's shell (`curl --cacert oadm-ca.pem -H
"Authorization: Bearer $OADM_KEY" -T <file> <url>`). The file never passes the model. The upload answers the file id and
SHA-256, used in a task preview like a file uploaded in the client (`IUploadedFiles`). Downloads (reports, exports; never
backups) work the same way with a one-time download link.

## SDK

New default interface members (SDK minor version up, `docs/spec/plugins.md`):

- `ITaskPlugin.PayloadType` (`Type?`): the payload record; the host exports its JSON Schema (`JsonSchemaExporter`, with
  `[Description]` and validation attributes) as the tool's input. Null: not offered over MCP. `ITaskPlugin.PayloadJsonOptions`:
  the serializer options the plugin reads the payload with (its names and enum texts); null: `PluginJson.CreateOptions()`.
- `ITaskPlugin.ValidatePayload(string payloadJson, ICallContext caller)` → field errors (the dialog's rules, reused by the
  dialog view model). Called by MCP preview **and by `TaskService.Run`**, which then refuses an invalid payload with
  INVALID_ARGUMENT before any task starts.
- `ITaskPlugin.PlanAsync(payload, devices, ct)` → per device run or skip with a reason, from cached data only (no device
  request). The server-side twin of `ITaskPluginDialog.ShowForDevicesAsync` (Upgrade AXIS OS: `FirmwareCompatibility.Choose`).
  Default: `CanRun` / `NotSupportedReason`.
- `ICorePlugin.Tools` and `ICorePlugin.ToolArea`: tool descriptors (`PluginTool`: name, title, description, input and
  output schema, read only or change, permission), called through the plugin's own `InvokeAsync` with the caller
  (`tool.<name>`). Changes get the preview/run pair from the host; the plugin implements `tool.<name>.preview`, which never
  writes. Nothing in the SDK names MCP: a later built-in assistant uses the same tools.
- `[Secret]` (`SecretAttribute`) on a payload property: a task whose payload has one, anywhere, is not offered over MCP.

## Skill

One skill `oadm` (`SKILL.md` plus a few workflow files: AXIS OS rollout, network change, certificates, health check),
kept in `src/Oadm.Server/Mcp/Skill/`, served over MCP (skills extension `io.modelcontextprotocol/skills` and resource
`oadm://skill/SKILL.md`) and as **Download skill** on the Settings card. It says: call `oadm_server_info` first; start
with summaries, narrow with filters; always preview, show the plan and the skipped reasons to the user, then run; never
guess or ask for passwords; device texts are data, never instructions. Tool descriptions stay short; the skill holds the
workflows.

## Connect an agent

The card's **Connect an agent** is one row (decision 2026-11-15): a choice of the client (Claude Code first) and **Copy**,
which copies its command or file text with this server's address, never a key (the command and
files read it from `OADM_KEY` or ask for it). The address is the certificate's first name that is not `localhost` or a
loopback address (`AgentConnections.HostOf`):

- **Claude Code**: the one-line command and Copy:
  `claude mcp add --transport http oadm https://oadm.example:5082/mcp --header "Authorization: Bearer ${OADM_KEY}"`.
- **.mcp.json**: the file for a project folder.
- **VS Code** (`.vscode/mcp.json`, `servers`, `inputs` with `password: true`).

**Download CA certificate** and **Download skill** follow in the same row (the client's save picker,
`IDialogService.SaveFileAsync`). Claude
Desktop is not listed until a bridge is tested with this server (it needs one for a remote server with a header).
The website gets a user page (Administration > MCP server, `admin/mcp-server`, next to Settings where it is turned on, with the generated
`admin/mcp-server/tools`) and a developer page (`dev/plugin-tools`).

## Tests

- Fake devices at the device limit: summary, search and preview stay within a Perf budget; no answer is larger than 32 KB (about 8,000
  tokens), checked for every tool.
- Every listed tool has annotations, an input schema with `additionalProperties: false` and an output schema; every write
  tool has a preview; a role without a permission does not list its tools (a Viewer key lists no write tool).
- Handles: once, 10 minutes (`FakeTimeProvider`), bound to the key; a cursor of another key is refused.
- No secret in any answer or log (a scan for the test passwords and keys over all tool outputs).
- Invalid input of every bundled task's schema gives field errors and starts no task; `TaskService.Run` refuses it too.
- The same caller sees the same devices over MCP and gRPC (device groups).

## Stages

1. Endpoint, Settings card, PKI policy MCP server, access keys, `oadm_server_info`, devices summary/search/get, tasks
   list/get, logs (read only).
2. SDK members, `ValidatePayload` in `TaskService.Run`, task catalog/preview/run, schemas for the bundled task plugins,
   upload and download links.
3. Tags, sites, compliance, event rules, the bundled core plugins' tools.
4. Skill, Connect an agent, website pages.

## Stage 1 as built

- **Code**: the tools in `src/Oadm.Server/Tools/` (`ToolRegistry` the list and the calls with `ToolCaller`, `ToolSchema`
  the schemas and their check, `ToolCursors`, `ServerInfoTool`, `DeviceTools`, `TaskTools`, `LogTools`); MCP in
  `src/Oadm.Server/Mcp/` (`McpListener` opens and closes the port, `McpEndpoint` the web host and its checks, `McpAccess`
  the key's `ToolCaller`, the per-key limits and the audit of refused keys, `McpToolAdapter` the two MCP handlers,
  `McpGrpcService` the card), `Auth/InProcessAccess.cs` (`InProcessCalls`: a gRPC method called in process with the rule of
  `AccessPolicy`, the caller in the call's user state and in `CallerContext`, the interceptor's answers),
  `Auth/AccessKeyGrpcService`, `Devices/DeviceSearch` (`DeviceService.Search`: the filter, the sort and the page on the
  server); core `AccessKeyStore` (table AccessKeys, migration `AgentAccessKeys`, which replaced the first design's rows of
  `AuthTokens` of migration `AccessKeys`); PKI `PolicyKinds.McpServer` (shown as "MCP server"), `PkiServerCertificates`;
  SDK 0.13.0 `IServerCertificates`, `HostServers.Mcp`; client `Settings/AiPageView` (the page MCP & Assistant, card MCP),
  `Settings/AccessKeysViewModel` (the list), `Settings/AccessKeyFormWindow` and `AccessKeyFormViewModel` (Create access key).
  Contracts `mcp.proto` (`McpService`, `AccessKeyService`) and `Mcp/McpTexts` (the card's texts, server and client).
- **Origin**: a request with an `Origin` passes only when it is the request's own scheme, host and port and the host is a
  name or address of the certificate (with DNS rebinding the browser sends the page's name as Origin and Host).
- **SDK**: `ModelContextProtocol.AspNetCore` 2.3.1 (its runtime packages are the host's 10.0.13), `Stateless = true`,
  `McpServerOptions.ProtocolVersion` "2026-07-28" (an `initialize` is refused with -32022), the SDK's own handlers for
  `tools/list` and `tools/call`. The SDK checks `Mcp-Method` and `Mcp-Name` (-32020); OADM refuses a request without
  `MCP-Protocol-Version` (400, -32020), which the SDK would serve as an older revision. GET is 405, other paths 404.
- **Who calls**: the key itself with its role and devices of now, user name "MCP <key name>" (also the task owner and the
  audit user, `AccessKeyStore.AgentNameOf`, `AuthenticatedCaller.IsAccessKey`; `AuthService.Me` answers the key's name,
  role and devices). A tool is listed when `AccessPolicy` allows every gRPC method
  the tool calls (so the logs need "See the server log" and all devices, the audit log the Administrator role); otherwise a
  call answers `isError` with the refusal. Calls per key: 20 a second (429, Retry-After 1) and 4 waiting for their answer at
  once (429 "Too many calls at once. Wait for an answer.", Retry-After 1); 10 changes a minute (stage 2). A refused key is
  audited once per address (IPv6 per /64) and key a minute, the count in the detail, and the whole port starts at most 30
  such entries a minute (`McpAccess.MaxRefusalAuditsPerMinute`; later refusals of that minute are not audited); an ended
  key keeps its row until the hourly cleanup, so its refusal names "MCP <key name>" ("The key has ended. 2
  refused calls"); after 50 refused keys from an address (IPv6 per /64) in 5 minutes it waits until they end (429,
  Retry-After 300), like logins.
- **Answers**: structured content and the same JSON as text, at most 32 KB (rows are cut to fit, `next_cursor` goes on);
  errors are `{"error": ..., "errors": [{"field": ..., "error": ...}]}`. Every argument is checked against the published
  input schema (unknown fields, types, lengths, ranges, `enum`, `uuid`, `date-time`). Device rows: `id`, `address`,
  `host_name`, `upnp_name` (each as the device has it, empty when not known; OADM never picks one name), `model`,
  `firmware` (the AXIS OS version), `status` (`Ok`, `Unreachable`, `CredentialsRequired`, `PasswordNotSet`,
  `CertificateChanged`, `SiteOffline`, `Unknown`) and the asked `fields`; the filter's `search` matches the address, the
  host name and the UPnP name (and serial, model, AXIS OS version, site, tags); a list of the filter matches any of its
  values; `firmware` takes `below` and `at_least`; sites are matched by name, "This server" for the server's own network; a
  search sorts by `address` (IPv4 in number order) unless `sort` says `host_name`, `upnp_name`, `model`, `firmware`,
  `status` or `last_seen` (a leading `-` descending); the summary's `group_by` defaults to status. A device or task outside
  the caller's group answers like an unknown one. The device tools call `DeviceService.Search` (docs/spec/grpc.md): the
  filter is applied on the server to the devices of the view, only the page is mapped and without the API lists
  (`oadm_devices_get` asks for them), the server info counts the view's cached ids. Log lists answer `more` and
  `next_cursor`, never a total (rule 3).
- **Cursors**: the last row's sort key and id (`ToolCursors`), signed with a key of the server process (a restart ends them)
  and bound to the access key and the query (the arguments without `cursor` and `limit`); a cursor of another key or
  search, or one that was changed, answers "This cursor does not belong to this search. Search again without it." The next
  page starts after that row in the sort order, so rows added, removed or changed elsewhere never move it: devices by the
  sort's key and id (`DeviceService.Search` `after_key`, `after_id`), tasks newest first by their start and id, the audit
  log by its entry id (`AuditService.List` `before_id`), the server log by the line's time and how many lines of that
  millisecond were shown (its lines have no id; `ServerLogService.List` `not_after`).
- **Card** (MCP on the page MCP & Assistant): setting `Mcp` (on, port 1025 to 65535, Listen on), audited "Changed MCP server"; every user
  sees the state and downloads the CA certificate (`oadm-ca.pem`, the issuers of the server's certificate). Off, PKI is not
  asked and the certificate line says "Turn on gets it from the PKI policy MCP server."; the host loads the certificate only while it is
  on (`IServerCertificates.LoadAsync`), and PKI's first load gives a policy without one its own "MCP Server Certificate"
  (`PkiService.EnsureMcpServerCertificateAsync`, docs/spec/pki.md "Server certificates"). On, the port
  opens when PKI runs and its policy picks a usable certificate; otherwise the status says why ("PKI is not running. The
  server log says why.", the certificate's problem, "Interface ... is not available", "Port 5082 cannot be opened: ..."), tried
  again every 30 s, on a change of the policy or the Certificates list and when a plugin is turned on or off. Windows service: the rule
  "OADM Server (MCP server, TCP 5082)" while it listens.
- **Keys**: at most 100 on the server; a restore ends them like the sessions (backups hold no keys).
- **Measured** at 100,000 devices with 100 APIs each and 50,000 tasks (`McpScaleTests`, Debug in a container, 2026-11-14):
  with `DeviceService.Search` the summary 2.4 s, a search page 1.3 to 1.7 s, a filtered search 1.4 s, one device 28 ms and
  the server info 0.7 s (with `DeviceService.List` of every device: 2.3, 3.0, 2.9, 3.5 and 3.7 s); the task list 1.6 s, one
  task 1.3 s, the logs milliseconds; every answer under 32 KB.
- **Tests**: `tests/Oadm.Server.Tests/Mcp` (`McpServerTests`: off by default, PKI, origin and DNS rebinding, methods, body,
  headers, the official client, 401 and its audit, an ended key in the audit log, 429 per second and at once, no gzip;
  `McpToolTests`: schemas and annotations, roles, no tool reaches what stays GUI only (rule 5), device texts
  only as JSON values (rule 6), keys ending, field errors, paging and sort-key cursors (also an empty filter, the three
  names of a row, both logs with new lines between pages), the summary, the same devices over MCP and gRPC, tasks, the
  audit log; `DeviceSearchTests` (`DeviceService.Search`);
  `McpSecretTests`: no password, token or key in any answer, audit entry or log line), `Tools/ToolRegistryTests` (two tools
  called in process with a caller object and no request; the tools name nothing of MCP or ASP.NET), `Perf/McpScaleTests`, PKI
  `McpServerPolicyTests` (its own certificate on the first Turn on, also without OADM PRO; nothing at a PRO start while it is
  off; the user's choice stays; the host's full name), `McpAccessKeyTests` (access keys: Administrator only, the field
  errors with their field, an Administrator key has all devices, the key's identity in the audit log, task owner and server
  info, a role or group change applies on the next call, deleting a role or group a key names is refused, Delete and the end
  date end a key), client `AiAgentsTests` (headless, screenshots: the card, the Create access key form and its field errors,
  the key shown once with Copy, the list, Delete asks first, the Connect an agent choice with Copy).

## Stage 2 as built

- **SDK 0.13.0** (`src/Oadm.Sdk/Tasks/TaskPayloads.cs`, `ITaskPlugin`): `PayloadType`, `PayloadJsonOptions`, `ValidatePayload`
  (`PayloadError` per JSON field), `PlanAsync` (`TaskPlan`, `TaskPlanDevice`, default `TaskPlan.ByCanRun`), `SecretAttribute`.
  The host's `Tasks/TaskPayloadSchemas` exports the schema once per type: objects without other properties, the attributes'
  bounds, the host's bounds for a text, number or list without one (4,096 characters, the 32-bit range, 100,000 items); no
  schema (not offered) for a secret, a value of any type, a reference or a type the exporter cannot describe.
- **gRPC** (docs/spec/grpc.md): `TaskService.Run` asks `ValidatePayload` before any task starts (INVALID_ARGUMENT with the
  first text, trailer `oadm-field`; a check that throws refuses the run) and answers `batch_id`; `TaskService.Plan` (the
  preview: the checks of Run, the field errors, the filter resolved in the caller's view in address order, per device run
  or skipped by `PlanAsync`, the task name, the warnings; nothing written); `ListTaskPlugins` with `with_payload_schema` and
  a `filter` (counts only within it).
- **Tools** (`Tools/TaskRunTools`): `oadm_tasks_catalog(filter, task)` (the offered tasks by name with `can_run`,
  `cannot_run` by reason and `input_schema`; schemas that do not fit 32 KB are left out with a note, `task` gives one in
  full), `oadm_task_preview(task, filter, input)` (the input against the task's schema, then the plugin's rules, field errors
  as `input.<field>`; the plan with the first 20 devices, the skipped ones by reason and the warnings; a handle when a
  device would run), `oadm_task_run(handle)` (`destructiveHint`; `TaskService.Run` with the preview's devices and payload:
  every check again; batch and task count), `oadm_files_upload_link(name, size)` (a file name without a folder, the exact
  size). Handles and links (`Tools/ToolHandles`): random, bound to the caller's binding key, 10 minutes, once, at most 20
  of a kind per caller, in memory only; another key's handle answers like an ended one.
- **MCP** (`Mcp/McpFiles`, `McpEndpoint`): the link is the server's own name (the MCP server certificate's first DNS name,
  else its first address) and the MCP port, never the request's `Host`; a file name has letters, digits, spaces and
  `. - _ ( )` only. `PUT /files/<token>` with the same key: the link's caller only, exactly its size
  (400 otherwise), through `FileService.Upload` in process (its limits; the tasks of the same key read the file), answers
  `file_id`, `name`, `size`, `sha256`, audited "Uploaded file" as "MCP <key name>". Every change an agent causes is
  audited as "MCP <key name>" (the key's caller name). Changes per key: 10 a minute (429 "Too many changes with this access key. Wait a
  minute.", Retry-After 60; counted on `Mcp-Method` tools/call and `Mcp-Name` of a tool that starts tasks).
- **Offered tasks**: Restart and Use OADM NTP server (no settings), Date and time (`DateTimePayload` with its
  descriptions, `PayloadValidator` at the payload's fields; the year is left to the device as in the task). Users has a
  `[Secret]` password and is not offered. Also offered: Upgrade AXIS OS (uploaded files only, factory default, allow
  downgrade; `PlanAsync` is `FirmwareCompatibility.ChooseFor` from the cached model and version; no online download, which
  needs the user's MyAxis sign-in), Network settings, Assign IP address, Applications (install, remove, start, stop),
  Enable and Disable HTTPS, Enable and Disable IEEE 802.1X, Renew certificates. The dialogs and
  the previews share their warning texts (`FirmwareWarnings`, `NetworkWarningTexts`, `PkiTaskIds`). Not offered (fail
  closed): application license keys and settings, Install certificates (secrets), Install CA certificates (a new trust
  anchor on the devices: the dialog shows each certificate to the person), Delete and View certificates (choices only the
  dialog makes), EST setup, Users. The PKI enable tasks have no clock question over MCP: a device with a late clock
  refuses. The schema export also describes maps (Network's devices by id: `propertyNames` uuid, at most 100,000).
- **Tasks and files**: `oadm_tasks_cancel_preview`/`_run` (a batch or up to 200 task ids; counts what stops and what
  cannot; only the caller's devices; one cancel takes at most 200 tasks and runs at most 60 s, `more_after_this` and
  `left` say to preview again); a task preview names each uploaded file it uses with its SHA-256 (`files`), and an
  application install warns that the package runs on the devices; `oadm_files_list` (the files this key sent, newest first, 50 a page;
  `FileService.List`) and `oadm_files_delete_preview`/`_run`.
- **Download links**: `GET /files/<token>` on the MCP port serves a `PlannedDownload`: once, 10 minutes, bound to the key,
  the same key check as uploads, audited "Downloaded file". No tool hands one out yet;
  never a backup (backups stay GUI only).
- **Request body**: at most 1 MB for every request, `/files/` too, until its key passed and its upload link was taken;
  only then `McpFiles.ReceiveAsync` raises the limit to the link's exact size.
- **Tests**: `McpTaskToolTests` (the catalog and a secret task, a preview that writes nothing, the handle of another key,
  once, 10 minutes, invalid input from the schema and from the plugin, the same refusal through `TaskService.Run`, upload
  links, 10 changes a minute), `McpToolTests` and `McpSecretTests` over every tool, the canary
  case of `TaskService.Plan`.

## Stage 3 as built

- **Devices** (`Tools/DeviceChangeTools`, `Tools/SettingsTools`): `oadm_devices_snapshot(device_id)` (one JPEG as an image
  result, Live view permission, at most 1280 px wide; over 1 MB it is taken again smaller, then at 640 px; the camera's
  answer is read up to 4 MB; snapshots are audited "Took a snapshot" once per caller and minute, with the counts of pictures and devices);
  `oadm_devices_refresh_preview`/`_run`; `oadm_devices_remove_preview`/`_run` (a filter is required, an empty one is
  refused); `oadm_devices_credentials_preview`/`_run` (an entry of the credential list by its name, never a password; the
  credential list is the Administrator's, a name two entries share is refused); `oadm_devices_add_preview`/`_run` (exactly
  one of a range (at most 65,536 addresses, as the client), up to 100 addresses or `try_waiting`; `keep_waiting` puts
  addresses that do not answer on the waiting list; IP addresses only, no names, schemes or ports, and only inside a
  network of the server's own interfaces or of a connected site, otherwise a field error names the allowed networks; the
  preview only looks (it sends no login and stores nothing), waits at most 90 s and then says the scan is incomplete; it
  acts outside OADM, so it is not announced as a read and it counts as a change; the run logs in with the credential
  list and adds only the devices that logged in, at most 200 a run, never one with factory default settings or a failed
  login); `oadm_settings_get` (what the Settings page shows, without secrets). A preview is listed
  only for a caller who may run its change. Refresh is not destructive; Remove and Set credentials are.
- **Tags, sites, compliance, event rules** (`Tools/TagTools`, `SiteTools`, `ComplianceTools`, `RuleTools`):
  `oadm_tags_list` (device counts in the caller's group), `oadm_tags_assign_preview`/`_run` (add and remove tags on a
  filter's devices, the first 20 devices shown; destructive because it can remove tags); `oadm_sites_list` (no node id,
  address or fingerprint; waiting nodes only counted); `oadm_compliance_summary` (per check: pass, fail, unknown of the
  filter's devices, worst first, counts only) and `oadm_compliance_checks`; `oadm_rules_catalog` (the events and actions a
  rule can use, with their fields), `oadm_rules_list`, `oadm_rules_get`, `oadm_rules_save_preview`/`_run` (create or
  change; the rule editor's checks and texts through `EventService.CheckRule`, nothing saved by the preview; a new rule is
  saved off unless `enabled`; the preview names actions that change devices and warns about one on every device),
  `oadm_rules_delete_preview`/`_run`, `oadm_events_log` (`more` and a cursor, no total). Passwords, secret headers, code
  and file fields of actions are never shown, and a rule that needs a new one answers "Set this in OADM.". A change keeps
  an action's stored passwords, tokens and hidden headers only while the change touches nothing but the action's content
  (message text, code, multi-line text); any other change of such an action is refused with "Set this in OADM.". Rule answers
  show an address as scheme, host and port only (an address given back unchanged keeps the stored one), and a URL with a
  login in it is refused. The save preview names every outside destination ("Sends event data to ...") and warns again
  of a new one that is not a saved recipient; the destinations themselves are in `destinations` (kind, the scrubbed
  target, a saved recipient's `name`, saved recipient, new). Warnings and errors are fixed sentences; rule, recipient and device names
  appear only in their own fields, the server's problem text in `problem`. A rule naming more than 200 devices is
  changed in OADM.
- **Plugin tools** (`Tools/PluginTools`, a tool source of the registry that follows the plugins that are on): core plugins
  bring tools through `ICorePlugin.Tools` (SDK 0.13.0: `PluginTool`, `PluginToolPage`, `PluginToolErrors`, `ToolArea`, which
  defaults to the plugin id without `oadm.`). The host names them `oadm_<area>_<name>`, refuses a name that a host tool
  has, leaves out a tool without a strict schema, lists them only while the plugin is on and the caller has the tool's
  permission and the plugin's method rules, calls `PluginService.Invoke` in process with the caller, refuses an answer over
  32 KB and never shows a plugin's exception text. Bundled: `oadm_pki_certificates`, `oadm_pki_policies`,
  `oadm_pki_certificate_create_preview`/`_run` (the PKI page's own create check, no key material in any answer),
  `oadm_ntp_status`, `oadm_dhcp_status`, `oadm_dhcp_leases`, `oadm_hardening_results`.
- **gRPC** (docs/spec/grpc.md): `DeviceService.Resolve`, `DeviceService.SetStoredCredentials`, `LiveViewService.GetSnapshot`,
  `TagService.PlanDeviceTags`, `ComplianceService.Summarize`, `EventService.CheckRule`, `ListEventLogRequest.before_id` and
  `skip_total`; each with its `AccessPolicy` rule and, when it takes device ids, its device group canary case.
- **Tests**: `McpDeviceChangeTests`, `McpAreaToolTests`, `McpRuleToolTests`, `McpPluginToolTests`, the plugins' `ToolTests`,
  and `McpToolTests` / `McpSecretTests` over every tool; Perf at 100,000 devices (`McpDeviceChangeScaleTests`: previews 1.2
  to 2.3 s, a removal 4.8 s; `McpAreaScaleTests`: tag preview 2.9 s, tag run 15 s, tag list 1 s, compliance summary 2.9 s;
  Hardening results 43 ms a page; every answer a few KB).

## Stage 4 as built

- **Skill** (`src/Oadm.Server/Mcp/Skill/`: `SKILL.md`, `rollout-axis-os.md`, `network-change.md`, `certificates.md`,
  `health-check.md`, embedded): `Mcp/McpSkill` serves them as the resources `oadm://skill/<file>` to any valid key and
  declares the extension `io.modelcontextprotocol/skills` in the server capabilities; `McpService.GetSkill` answers them
  as `oadm-skill.zip` (every signed-in user) for **Download skill**.
- **Card**: **Connect an agent** is one row: the client (Claude Code, `.mcp.json`, VS Code) and Copy with this
  server's address (the certificate's first name, a DNS name before an address) and port while the server runs, then
  **Download CA certificate** and **Download skill**.
- **Website**: `admin/mcp-server` (turn it on, access keys, connect an agent, the skill, what agents can and cannot do) and
  `dev/plugin-tools` (how a plugin adds tools).
- **Tests**: `McpSkillTests` (resources, the ZIP, the tool names the skill uses exist, the Wording rules), client
  `AiAgentsTests` (the configurations, headless screenshots).

## Open points

- Client support for the MCP tasks extension: if it spreads, `oadm_task_run` may answer a task handle instead of polling.
- Not offered yet: recipients, changing compliance checks, turning a rule on or off by itself; the waiting list keeps no
  tags or site for a device an agent puts there.
- The skills extension is declared without fields; when its schema settles, `McpSkill` may list the skill there too.

