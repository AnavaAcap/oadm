# Assistant (chat with an AI inside OADM)

Status: **built** (decisions of 2026-11-15; first version).

Part of the OADM spec (`CLAUDE.md` "Feature specs"): read it before you change this area, update it in the same change.

A user chats with an AI model in a side panel of the client, and the model works in OADM through the same tools AI agents
use over MCP (`docs/spec/mcp.md`), with the signed-in user's rights. The server talks to the provider; the client only
shows the chat.

## Rules that never break

1. **The user's rights, nothing more.** Every tool call runs as the signed-in user (role, device group, audit log), through
   the tool registry (`ToolRegistry`, `ToolCaller` with the user's `AuthenticatedCaller`): the same checks as the client.
   The assistant has no rights of its own.
2. **A person presses Run.** A change is always a preview the model asks for; the panel shows the plan as a card (what,
   how many devices, the skipped ones with their reasons, the warnings) with a **Run** button. Only the user's click runs
   it (the run tool with the preview's handle). The model never calls a run tool: run tools are never offered to it.
3. **No secrets.** The provider's API key is stored encrypted with the master key (like every secret), never sent to a
   client, never logged. No tool takes or returns a password, key or token (`docs/spec/mcp.md` rule 4).
4. **Device texts are data.** Names, tags, event texts, log lines, tool answers and file names go to the model as data,
   never as instructions (the system prompt says so, like the skill); a text that tries to give an order never changes
   what a run does, because every run is the user's click on a preview.
5. **Nothing is kept.** A chat lives only while its panel is open; closing it or signing out forgets it. The server keeps
   no chat text and no picture. The audit log keeps every change and every camera picture as always.
6. **Off by default**, and only administrators set it up.
7. **Pictures reach the provider only when an administrator allows it.** A camera picture is always shown to the user in
   the panel; the model sees it only with **Send pictures to the model** on (off by default).

## Where it runs

- **Server** (`src/Oadm.Server/Assistant/`): the conversation loop (`Microsoft.Extensions.AI` `IChatClient` with function
  invocation done by OADM, not by the library): the user's message, the tool list of the user (the registry's list for that
  caller, read and preview tools only, `AssistantTools.Offered`), the model's answer, tool calls run in process, until the
  model answers in text. One conversation per open panel, in server memory only (`AssistantConversations`), ended when the
  panel closes (`End`), the session ends, the assistant is turned off or gets another provider or model, or after 30
  minutes without a message. A Run reaches the model with the next message, as data.
- The texts and limits the client shows too live in `Oadm.Contracts.Assistant.AssistantTexts`.
- Providers (`AssistantProviders`): Anthropic (the official `Anthropic` SDK's `IChatClient`), OpenAI and
  OpenAI-compatible (`Microsoft.Extensions.AI.OpenAI`), Ollama (`OllamaSharp`). Only the server references them.
- **Client**: the panel. gRPC `AssistantService` (`src/Oadm.Contracts/Protos/assistant.proto`): `Chat` server-streaming
  (the answer's text as it comes, short tool lines, preview cards, pictures, tables, the end of the answer), `Stop`, `Run`
  (a preview's handle; streams the run's tasks like any task), `End`, `GetSettings`, `SetSettings`, `Test`. Run goes
  through the same tool registry (the run tool of the preview), not through the model.

## What the user gives with a message

- **The selection.** The panel sends the ids of the devices selected in the device grid with every message (at most
  10,000; more: "Select at most 10,000 devices for the assistant."). The model sees the count and can call
  `oadm_selection` (ids, paged like `oadm_devices_search`) and use `selection` as a filter of the device tools ("these
  cameras"). Devices outside the caller's group are dropped as usual.
- **Files.** **Attach file** in the panel uploads through `FileService.Upload` (the same upload as the task dialogs, its
  limits and audit); the message carries the file's id, name and size, never its content. The model uses the id in a task
  preview (firmware, ACAP). The file is deleted with the other uploads as usual, not when the chat ends.

## What the panel shows

- The model's answer streams as Markdown (paragraphs, lists, bold, code, tables), with **Copy** on every answer.
- **Tool lines**: one short line per tool call ("Looking at devices", "Checking 812 devices for the upgrade").
- **Preview cards** with **Run** and **Cancel**; several previews in one answer show one card each. After Run the card
  follows its tasks live (running, done, failed, from the task stream the client already has) and links to the tasks pane.
- **Pictures**: a camera picture (`oadm_devices_snapshot`) shows in the answer, click to enlarge; never stored.
- **Tables**: a device list the model got from a tool shows as a small table (name, address, model, status) where a click
  on a row selects the device in the device grid; at most 50 rows, then "+N more" with **Select all in the grid** (every
  matching id up to 10,000 comes with the table).
- **Stop** ends the answer that is streaming; the conversation stays.
- **New chat** clears it. An empty panel shows four start suggestions ("Which devices are offline?", "Which devices have
  vulnerabilities?", "Which cameras run old AXIS OS?", "Show me a picture of a camera"), clicked they are sent.
- A user's first message after the panel opened shows once: "Your messages and the data the assistant reads go to
  <provider>." (not for Ollama).

## Questions about OADM

`oadm_docs_search(query)`: the user docs of oadm.app (the pages under Get started, Devices, Sites, Device tasks, Plugins,
Administration; not the developer pages) ship with the server as a search index (`Assistant/DocsIndex`, built from
`website/src/content/docs` at build time into the server's resources). Answers title, page address on oadm.app and the
matching passage (at most 5 hits, 32 KB). A registry tool like every other, so MCP agents get it too.

## Settings (card **AI**, section **Assistant**, below the section **MCP**)

Administrators only; changes audited ("Changed assistant").

- **Turn on** / **Turn off**.
- **Provider**: Anthropic, OpenAI, Ollama, or OpenAI-compatible (an address and a key: LM Studio, vLLM, Azure OpenAI).
- **API key** (not for Ollama): stored encrypted, shown as "Saved" afterwards, never sent back; **Address** for Ollama and
  OpenAI-compatible (`http://10.0.0.5:11434`).
- **Send pictures to the model**: off by default.
- **Daily limit**: tokens a day for the whole server (default 2,000,000) and, optionally, per user; **Used today** shows
  the count. At the limit the panel says "The assistant reached today's limit. It works again tomorrow." (reset at
  midnight server time).
- No model here (decision 2026-11-15): each user picks it in the panel.
- **Test**: checks the address and key by reading the provider's model list (Ollama, OpenAI-compatible without a list:
  one short call); "The provider answered." or the provider's error in plain words.
- A note under the provider for the cloud ones: device names, addresses and other data of your devices go to that
  provider. Ollama keeps everything in your network.
- Who may use it: permission **Use the assistant** (on for Administrator, off for the other built-in roles).

## The panel

- A title bar button (next to Help, icon chat) opens the panel **Assistant** on the right, over every page; it stays open
  while the user moves between pages. Hidden when the assistant is off or the role lacks the permission.
- Bottom: the message box (Enter sends, Shift+Enter a new line), **Attach file**, the selection count ("12 devices
  selected", nothing without a selection), **Send** / **Stop**.
- **Model** (decision 2026-11-15: chosen only in the panel, by each user): a picker next to Send with the provider's
  models (`ListModels`; editable when the provider offers no list, e.g. some OpenAI-compatible servers). The choice is
  kept per user on that client (client settings); first time: the provider's first model, for Anthropic
  `claude-opus-5-5` when listed. Every message carries the model; changing it keeps the conversation. A model that
  cannot call tools answers once "This model cannot use tools. Choose another one."

## Limits

Per login session one conversation at a time (a new chat ends the one before) and one answer at a time; at most 20
tool calls per message (then the model must answer without tools); tool answers at most 32 KB (the tools' own
limit); the model's context is cut from the oldest messages when it grows too long; a provider timeout of 120 s per
answer with all its rounds ("The provider did not answer. Try again."), 30 s for Test; a message at most 8,000
characters with at most 10 files; at most 5 pictures per message to the model, and a picture never stays in the later
context.

## Tests

- Fake `IChatClient` that asks for given tool calls: rights of a group user apply; a role without a permission gets no
  tool for it; run tools are never offered to the model; Run only from the user's call with a valid handle of that user.
- The selection reaches the tools (`oadm_selection`, the `selection` filter) and is cut to the caller's group.
- Pictures: shown to the client always, sent to the model only with the setting on.
- No secret in any answer, log line or audit entry (the scan of the MCP tests over the assistant's traffic).
- The daily limit with a `FakeTimeProvider` (reset at midnight), per server and per user.
- `oadm_docs_search` finds a known page.
- Headless panel tests with screenshots (empty with suggestions, an answer with a table and a picture, a preview card
  before and after Run); the settings card's validation below the fields.

## Open points

- Voice: not planned.
