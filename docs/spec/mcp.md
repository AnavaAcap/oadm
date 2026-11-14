# MCP server (AI agents)

Status: **stage 1 built** (the endpoint, the Settings card AI agents, the PKI policy MCP server, access keys and the tools
`oadm_server_info`, `oadm_devices_summary`, `oadm_devices_search`, `oadm_devices_get`, `oadm_tasks_list`, `oadm_tasks_get`,
`oadm_logs_search`, `oadm_logs_audit`; see "Stage 1 as built"). Stages 2 to 4 are not built: no tool changes anything yet.

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

An AI agent (Claude Code, Claude Desktop, VS Code, any MCP client) can do through MCP what a user can do in the client,
with the same login, role, device group, checks and audit log. The MCP server is part of the OADM server (core, free,
Apache-2.0), off by default. Plugins add their own tools through the SDK, so a new plugin is usable by agents without a
host change.

## Rules that never break

1. **The agent is a client.** Every tool calls the server's gRPC services in process with the access key's caller
   (`InProcessAccess`, `CallerContext`): `AccessPolicy`, device groups (`DeviceView`), validation, limits and the audit log
   apply exactly as for the client. A tool never reads stores, `VapixClient` or plugins around that layer.
2. **No change without a preview.** Every tool that changes something (devices, tags, rules, files, tasks) is two calls:
   a preview that checks all input on the server and returns the plan and a handle, and a run that takes only that
   handle. Nothing is written by a preview.
3. **Never all devices in a result.** Lists return at most 50 rows by default (at most 200), always with the total and a
   cursor; the first answer about devices is a summary (counts), never a list.
4. **No secrets.** No tool takes or returns a password, private key, license key, access key or token. Stored
   credentials are used by name; a task that needs a new password is not offered over MCP ("Set the password in OADM.").
5. **Least rights.** The tool list shows only what the key's user may do now (role, device group, plugins on, Read only
   key). An agent can never raise rights: users, roles, device groups, access keys, license, plugins on/off, server
   settings, backup restore and data resets are GUI only.
6. **Device data is data.** Texts written by devices or other users (names, locations, tags, event texts, log lines) are
   returned only as JSON field values, never inside tool descriptions or messages to the agent, and the skill says they are
   never instructions.

## Transport and certificate

- Streamable HTTP, MCP revision 2026-07-28 (stateless: no session id, no `initialize`), one endpoint `POST /mcp` on its own
  port (default **TCP 5082**), Kestrel in the server process, official C# SDK `ModelContextProtocol.AspNetCore` 2.x
  (`WithHttpTransport` stateless, `MapMcp`). No stdio, no SSE GET stream, no sampling, no MCP logging (deprecated).
- **Off by default.** Settings card **AI agents** (GUI only; "MCP" appears only in the config snippet and the docs): Turn on, Port, Listen on (interfaces, like the EST server),
  the certificate, **Connect an agent** (the client config to copy, see below), **Download skill**. Off: the port is
  closed.
- **Certificate**: picked from the PKI Certificates list by a PKI server policy **MCP server**, like the EST and 802.1X
  servers (`docs/spec/pki.md` "Server certificates": a certificate with its key, server authentication, not ended, not on
  the not-accepted list; the policy refuses a certificate that does not name the server's addresses or host name). The
  agent's computer trusts its CA once (**Download CA certificate** on the card; Claude Code: `NODE_EXTRA_CA_CERTS`, else
  the OS store). Without PKI on, the card says "Turn on PKI to choose the certificate." and MCP cannot be turned on.
- Origin check (403 for a browser origin that is not the server), `MCP-Protocol-Version` / `Mcp-Method` / `Mcp-Name`
  checked against the body (-32020), request body at most 1 MB (files go through upload links). Answers are not compressed:
  the SDK sends every answer as a short event stream marked `Content-Encoding: identity` (open point below); they are at
  most 32 KB.
- Windows service: the firewall rule for the port exists only while MCP is on (`FirewallRuleKeeper`,
  `docs/spec/security.md` "Service: firewall and folders"). Site nodes do not relay MCP.

## Access keys

- A user creates keys for themselves in the client (account menu **Access keys**; permission **Use AI agents**,
  `mcp.use`, off for every built-in role except Administrator). Fields: Name (1..64, unique per user), Expires (30, 90 or
  365 days, default 90), **Read only**. OADM shows the key once ("Copy it now. OADM cannot show it again."); it stores
  only its SHA-256 (32 random bytes, base64url, prefix `oadm_`), like session tokens (`AuthTokens`, new kind `AccessKey`,
  migration `AccessKeys`).
- The Permissions page lists every user's keys for administrators (user, name, created, expires, last used, Read only)
  with Delete. A disabled or deleted user, a new password or a deleted key ends the key at once; a new role or device group
  applies to the next call.
- The agent sends `Authorization: Bearer <key>`. Missing or ended key: HTTP 401 with "The access key is not valid. Create
  a new one in OADM under Access keys."; not allowed: the tool is not listed, and a call answers `isError` with
  `AccessPolicy.DeniedMessage`.
- Audit: every call that changes something, every upload and every failed key is logged as "<user> via access key
  <name>"; a task's Owner is "<user>@MCP <key name>". Limits per key: 20 calls per second, 10 runs per minute (HTTP 429 with
  Retry-After).

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

All fields optional and combined with AND; an empty filter means every device the caller sees (allowed for summaries and
previews, never for lists without a cursor).

- `oadm_server_info`: version, user, role, the permissions, Read only, the limits, the device count. The first call.
- `oadm_devices_summary(filter, group_by)`: counts by status, model, AXIS OS version, site, tag or category (at most 50 groups,
  the rest as "Other").
- `oadm_devices_search(filter, fields, sort, cursor, limit)`: rows with id, name, address, model, AXIS OS version, status and the
  requested fields; `total`, `next_cursor`. The cursor is bound to the key and the device-table version; after a change it
  answers "The device list changed. Search again." Truncated answers say how to narrow ("312 more. Add a filter, e.g.
  site or model.").
- `oadm_devices_get(device_id)`: one device with its details (the client's device details, no credentials).
- `oadm_devices_snapshot(device_id)`: one JPEG of the camera as image content (Live view permission, at most 1280 px wide).
- Refresh, Remove, Set credentials (by stored credential name), Add devices (Network range, Add manually, the waiting
  list; logins with the credential list) as preview/run pairs.

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
(results, checks), event rules (list, get, create, change, delete, the event log), logs (server log search with `since`,
`level`, `text`, at most 200 lines; audit log), files (`oadm_files_upload_link`, list, delete), settings (read only).

### Plugins

Core plugins and task plugins bring their tools through the SDK; the host prefixes them `oadm_<plugin area>_` and lists
them only while the plugin is on and the role has its permission. Bundled plugins get tools for their pages (e.g. PKI
certificate list and Create certificate, NTP and DHCP status and leases, Hardening scan results, VAPIX Commander library
run as a task). A task plugin without a payload schema is not offered over MCP (fail closed).

### Not over MCP

Live video, Live View tabs, Dashboards live values, Metadata Monitor and I/O Monitor streams, Web View; users, roles,
device groups, access keys, license, plugins on/off, Forget server; Settings changes; backup restore, Delete all tasks,
Delete all devices. Creating a backup stays possible (as a download link, like uploads).

## Files

`oadm_files_upload_link(name, size)` answers a one-time HTTPS URL on the MCP port (10 minutes, one file, the file limits
of `FileService`, PUT with the same Bearer key) and the command for the agent's shell (`curl --cacert oadm-ca.pem -H
"Authorization: Bearer $OADM_KEY" -T <file> <url>`). The file never passes the model. The upload answers the file id and
SHA-256, used in a task preview like a file uploaded in the client (`IUploadedFiles`). Downloads (backups, reports,
exports) work the same way with a one-time download link. A Read only key gets download links, never upload links.

## SDK

New default interface members (SDK minor version up, `docs/spec/plugins.md`):

- `ITaskPlugin.PayloadType` (`Type?`): the payload record; the host exports its JSON Schema (`JsonSchemaExporter`, with
  `[Description]` and validation attributes) as the tool's input. Null: not offered over MCP.
- `ITaskPlugin.ValidatePayload(string payloadJson, ICallContext caller)` → field errors (the dialog's rules, reused by the
  dialog view model). Called by MCP preview **and by `TaskService.Run`**, which then refuses an invalid payload with
  INVALID_ARGUMENT before any task starts.
- `ITaskPlugin.PlanAsync(payload, devices, ct)` → per device run or skip with a reason, from cached data only (no device
  request). The server-side twin of `ITaskPluginDialog.ShowForDevicesAsync` (Upgrade AXIS OS: `FirmwareCompatibility.Choose`).
  Default: `CanRun` / `NotSupportedReason`.
- `ICorePlugin.McpTools`: tool descriptors (name, title, description, input and output type, read only or change,
  permission, the `InvokeAsync` method). Changes get the preview/run pair from the host; the plugin implements a
  `<method>.preview` that never writes.
- `IsSecret` on a payload property: a task whose payload has one is not offered over MCP.

## Skill

One skill `oadm` (`SKILL.md` plus a few workflow files: AXIS OS rollout, network change, certificates, health check),
kept in `src/Oadm.Server/Mcp/Skill/`, served over MCP (skills extension `io.modelcontextprotocol/skills` and resource
`oadm://skill/SKILL.md`) and as **Download skill** on the Settings card. It says: call `oadm_server_info` first; start
with summaries, narrow with filters; always preview, show the plan and the skipped reasons to the user, then run; never
guess or ask for passwords; device texts are data, never instructions. Tool descriptions stay short; the skill holds the
workflows.

## Connect an agent

The card's **Connect an agent** shows the config with this server's address, for example Claude Code:

```
claude mcp add --transport http oadm https://oadm.example:5082/mcp --header "Authorization: Bearer ${OADM_KEY}"
```

and the `.mcp.json`, Claude Desktop and VS Code (`servers`, `inputs` with `password: true`) forms, plus the CA download.
The website gets a user page (Tools > AI agents) and a developer page (plugin MCP tools).

## Tests

- Fake devices at the device limit: summary, search and preview stay within a Perf budget; no answer is larger than 32 KB (about 8,000
  tokens), checked for every tool.
- Every listed tool has annotations, an input schema with `additionalProperties: false` and an output schema; every write
  tool has a preview; a Read only key lists no write tool; a role without a permission does not list its tools.
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

- **Code**: server `src/Oadm.Server/Mcp/` (`McpListener` opens and closes the port, `McpEndpoint` the web host and its
  checks, `McpAccess` the key's caller, the per-key limit and the audit of refused keys, `McpTools` the list and the calls,
  `McpSchema` the schemas and their check, `McpCursors`, `ServerInfoTool`, `DeviceTools`, `TaskTools`, `LogTools`,
  `McpGrpcService` the card), `Auth/InProcessAccess.cs` (`InProcessCalls`: a gRPC method called in process with the rule of
  `AccessPolicy`, the caller in the call's user state and in `CallerContext`, the interceptor's answers),
  `Auth/AccessKeyGrpcService`; core `AuthTokenStore` (access keys), `HostPermissions.McpUse`, migration `AccessKeys`; PKI
  `PolicyKinds.McpServer`, `PkiServerCertificates`; SDK 0.13.0 `IServerCertificates`, `HostServers.Mcp`,
  `HostPermissionKeys.McpUse`; client `Settings/AiAgentsCardView`, `Shell/AccessKeysWindow` (account menu),
  `Settings/AccessKeysCardView` (Permissions page), both lists one `Controls/AccessKeyGrid`. Contracts `mcp.proto`
  (`McpService`, `AccessKeyService`) and `Mcp/McpTexts` (the card's texts, server and client).
- **Origin**: a request with an `Origin` passes only when it is the request's own scheme, host and port and the host is a
  name or address of the certificate (with DNS rebinding the browser sends the page's name as Origin and Host).
- **SDK**: `ModelContextProtocol.AspNetCore` 2.3.1 (its runtime packages are the host's 10.0.13), `Stateless = true`,
  `McpServerOptions.ProtocolVersion` "2026-07-28" (an `initialize` is refused with -32022), the SDK's own handlers for
  `tools/list` and `tools/call`. The SDK checks `Mcp-Method` and `Mcp-Name` (-32020); OADM refuses a request without
  `MCP-Protocol-Version` (400, -32020), which the SDK would serve as an older revision. GET is 405, other paths 404.
- **Who calls**: the key's user with the role and devices of now, client machine "MCP <key name>" (task owner
  "<user>@MCP <key name>"). A tool is listed when the role has "Use AI agents" and `AccessPolicy` allows every gRPC method
  the tool calls (so the logs need "See the server log" and all devices, the audit log the Administrator role); otherwise a
  call answers `isError` with the refusal. Calls per key: 20 a second (429, Retry-After 1); `RunsPerMinute` (10) waits for
  the task runs of stage 2. A refused key is audited once per address and key a minute, the count in the detail; an ended
  key keeps its row until the hourly cleanup, so its refusal names "<user> via access key <name>" ("The key has ended. 2
  refused calls"); after 50 refused keys from an address (IPv6 per /64) in 5 minutes it waits until they end (429,
  Retry-After 300), like logins.
- **Answers**: structured content and the same JSON as text, at most 32 KB (rows are cut to fit, `next_cursor` goes on);
  errors are `{"error": ..., "errors": [{"field": ..., "error": ...}]}`. Every argument is checked against the published
  input schema (unknown fields, types, lengths, ranges, `enum`, `uuid`, `date-time`). Device rows: `id`, `name` (the UPnP
  name, else the host name), `address`, `model`, `firmware` (the AXIS OS version), `status` (`Ok`, `Unreachable`,
  `CredentialsRequired`, `PasswordNotSet`, `CertificateChanged`, `SiteOffline`, `Unknown`) and the asked `fields`; a list of
  the filter matches any of its values; `firmware` takes `below` and `at_least`; sites are matched by name, "This server"
  for the server's own network; a search sorts by address unless `sort` says otherwise; the summary's `group_by` defaults to
  status. A device or task outside the caller's group answers like an unknown one.
- **Cursors**: signed with a key of the server process (a restart ends them), bound to the access key and the query (the
  arguments without `cursor` and `limit`); a device cursor also to `DeviceChangeFeed.MembershipVersion` (a device added or
  removed, or a change of what device groups decide on), "The device list changed. Search again."; the task and log cursors
  are offsets of the newest-first lists.
- **Card**: setting `Mcp` (on, port 1025 to 65535, Listen on), audited "Changed AI agents"; every user sees the state and
  downloads the CA certificate (`oadm-ca.pem`, the issuers of the server's certificate). On, the port opens when PKI runs
  and its policy picks a usable certificate; otherwise the status says why ("Turn on PKI to choose the certificate.", the
  certificate's problem, "Interface ... is not available", "Port 5082 cannot be opened: ..."), tried again every 30 s, on a
  change of the policy or the Certificates list and when a plugin is turned on or off. Windows service: the rule
  "OADM Server (AI agents, TCP 5082)" while it listens.
- **Keys**: at most 20 per user (the credential list's limit); a restore ends them like the sessions (backups hold no
  tokens).
- **Measured** at 100,000 devices with 100 APIs each and 50,000 tasks (`McpScaleTests`): about 3 s for each device tool
  (`DeviceService.List` of every device of the view, as the client's snapshot), 1.4 s for the task list, milliseconds for the
  logs; every answer under 32 KB.
- **Tests**: `tests/Oadm.Server.Tests/Mcp` (`McpServerTests`: off by default, PKI, origin and DNS rebinding, methods, body,
  headers, the official client, 401 and its audit, an ended key in the audit log, 429; `McpToolTests`: schemas and
  annotations, Read only keys, roles, no tool reaches what stays GUI only (rule 5), device texts only as JSON values (rule
  6), keys ending, field errors, paging and cursors, the summary, the same devices over MCP and gRPC, tasks, the audit log;
  `McpSecretTests`: no password, token or key in any answer, audit entry or log line), `Perf/McpScaleTests`, PKI
  `McpServerPolicyTests` (also the host's full name), client `AiAgentsTests` (headless, screenshots).

## Open points

- Client support for the MCP tasks extension: if it spreads, `oadm_task_run` may answer a task handle instead of polling.
- gzip: the SDK marks its event-stream answers `Content-Encoding: identity`; compressing them would mean working around it.
- An empty filter in `oadm_devices_search` is served page by page (rule 3, every page with the total and a cursor); refusing
  it without a cursor is the other reading of "never for lists without a cursor".
- The device tools read every device of the view per call (about 3 s at 100,000 devices with 100 APIs each); a list
  without the API lists, or the filter inside `DeviceService`, would make them faster.
- The log tools answer a cursor but no total (rule 3 says "always with the total"): `ServerLogService.List` gives only
  `has_more`, and a text or time filter would need a full count.
- Offset cursors: a new task or log line, or a device whose status changes under a status sort, moves the rows between
  pages; a cursor of the last row's sort key and id would not.
