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
  panel closes (`End`), the session ends, the assistant is turned off or gets another provider or address, or after 30
  minutes without a message. A Run reaches the model with the next message, as data.
- The texts and limits the client shows too live in `Oadm.Contracts.Assistant.AssistantTexts`.
- Providers (`AssistantProviders`): Anthropic (the official `Anthropic` SDK's `IChatClient`), OpenAI and
  OpenAI-compatible (`Microsoft.Extensions.AI.OpenAI`), Ollama (`OllamaSharp`). Only the server references them.
- **Client**: the panel. gRPC `AssistantService` (`src/Oadm.Contracts/Protos/assistant.proto`): `Chat` server-streaming
  (the answer's text as it comes, short tool lines, preview cards, pictures, tables, the end of the answer), `Stop`, `Run`
  (a preview's handle; streams the run's tasks like any task), `End`, `ListModels`, `GetSettings`, `SetSettings`, `Test`. Run goes
  through the same tool registry (the run tool of the preview), not through the model.

## What the user gives with a message

- **The selection.** The panel sends the ids of the devices selected in the device grid with every message (at most
  10,000; more: "Select at most 10,000 devices for the assistant."). The model sees the count and can call
  `oadm_selection` (ids, paged like `oadm_devices_search`) and use `selection` as a filter of the device tools ("these
  cameras"). Devices outside the caller's group are dropped as usual.
- **Devices chosen with @.** Typing @ in the message box (the character stays out of the text) or the **Choose devices**
  button (devices icon) opens the shared device picker (`DevicePickerWindow`, search and filters as everywhere); OK makes
  the choice one chip in the box ("3 devices", or the device's address for one; a click opens the picker with that
  choice, x removes it), Cancel changes nothing. The ids go with the message in `mentioned_device_ids` (at most
  10,000), cut to the caller's view like the selection; the model gets their count and the first 100 ids as data
  (`devices_the_user_chose`). Try again and Edit keep them.
- **Files.** **Attach file** in the panel uploads through `FileService.Upload` (the same upload as the task dialogs, its
  limits and audit); the message carries the file's id, name and size, never its content. The model uses the id in a task
  preview (firmware, ACAP). The file is deleted with the other uploads as usual, not when the chat ends. Files dropped on
  the panel or pasted into the message box attach the same way (folders are skipped, at most 10 files).
- **A pasted picture** (a bitmap on the clipboard, no file) becomes a PNG upload "Pasted image N.png" like any file; it
  is never sent to the model as a picture (the model gets its id, name and size only).

## What the panel shows

- **Thinking**: right after Send, a small ring and "Thinking" where the answer will appear, until the answer's first
  event (text, tool line, card, picture, table) or its end; the notice where the data goes shows above it.
- The model's answer streams as Markdown (paragraphs, lists, bold, code, tables), with **Copy** on every answer.
  Short answers (decision 2026-11-15, the system prompt in `AssistantChats.SystemPrompt`): the answer first in one to
  three sentences, detail only when asked; never a repeat of the device table a tool showed; a table only when asked
  and no tool showed one; no guessing when no tool gives the data.
- **Try again** (icon, next to Copy) under the end of the last answer, also on its error or "Stopped." line: the last
  message again with its files, in place of its answer. **Edit** (icon, on hover left of the last message, or Up arrow in
  the empty box) puts that message and its files back into the box; sending it replaces that exchange, emptying the box
  drops the edit. Both send `replace_last`: the server drops its last user message and everything after it (only when
  the server had that message; a message refused before it was answered is sent as a new one, and so is one the server
  no longer has: "There is nothing to try again."). The replaced entries leave the list in one step.
- **Scrolling**: the list follows new content only while the user is at its end (within 40 px); scrolled up it stays and
  a round **Go to the latest** button (arrow down) floats above the message box; it goes to the end and follows again. A
  new message always goes to the end. One scroll per burst of changes.
- **Tool lines**: one short line per tool call ("Looking at devices", "Checking 812 devices for the upgrade").
- **Preview cards** with **Run** and **Cancel**; several previews in one answer show one card each. After Run the card
  follows its tasks live (running, done, failed, from the task stream the client already has) and links to the tasks pane.
  When every task ended with failures its line says so with the reasons ("11 done, 1 failed: Wrong password"; the most
  frequent first, at most 3 and "+N more"), and **Try the failed devices again** asks the server (`RetryFailed`) for the
  same preview tool with the same input restricted to the failed devices: a new card that needs Run like any other.
  The server keeps each card's preview tool and input (the selection written out as ids) for that.
  The card says what Run does from the preview itself, never from the model's text (`AssistantCards`): the count with
  its unit ("3 devices", "2 tasks", "1 rule"), one line per key field (a task's name, its input without secrets and its
  files; a rule's name, what it watches and does, where it sends, its problem; the tags added and removed; the files,
  rules or devices deleted or removed; a plugin's preview: its plain fields one level deep), each line at most 200
  characters, at most 12 lines with "+N more". Run is red for a delete or removal (`destructive`).
- **Pictures**: a camera picture (`oadm_devices_snapshot`) shows in the answer, click to enlarge; never stored.
- **Tables**: a device list the model got from a tool (`oadm_devices_search`, `oadm_selection`, `oadm_vulnerabilities`)
  shows as a small table (name, address, model, status) where a click on a row selects the device in the device grid; when
  the tool's filter asked about a fact of the grid, the 4th column shows it instead of the status, named like its grid
  column (Clock, Certificate expires, SoC, Vulnerabilities: `AssistantTable.detail_header`, `AssistantDeviceRow.detail`,
  `AssistantCards.DetailOf`); the panel shows the first 8 rows, the message carries up to 50, then
  "+N more" with **Select all in the grid** (every matching id up to 10,000 comes with the table).
- **Stop** ends the answer that is streaming; the conversation stays.
- **Clear** (header, eraser icon; was New chat) empties it and starts a new conversation. **Copy chat** (icon in the header) copies the conversation as Markdown: questions, answers, tool
  lines, notices, tables, pictures by device name and card summaries (title, count, what Run does, the state); only what
  the panel shows, so never a secret.
- **The empty panel**: the OADM mark, "What can I do for your devices?", chips with live counts of the client's devices
  ("1,284 devices", "12 offline", "3 with vulnerabilities"; only those above zero; a click asks about them), and at most
  four suggestion cards (icon and one line, a click sends it): with a grid selection first ("Restart the 3 selected
  devices" only for a role that may run Restart, "Show a picture of the selected camera" for one), then a short list for
  the page the user is on (Devices, PKI, Event rules, Compliance, Sites, Logs), then the general ones ("Which devices are
  offline?", "Which devices have vulnerabilities?", "Which cameras run old AXIS OS?", "Show me a picture of a camera").
  The counts come from `AssistantDeviceIndex`: kept per change of the device store (O(changed devices)), counted once
  when the panel opens or a snapshot or a new vulnerability list arrives, nothing while the panel is closed.
- **Device links**: an address, host name or serial number in an answer that names exactly one device of the client's
  store is a link (teal): a click selects it in the device grid; the right button shows Select, Open live view and Open
  web interface as the role allows (the grid's checks). Found per word of the answer (O(text), independent of the device
  count).
- **Tables**: device tables and Markdown tables have **Copy as CSV** and **Save as CSV** (icons below them). A device
  table exports every device of the table the client knows (up to 10,000) in the columns of Export devices, only for a
  role with Export devices; a Markdown table its cells.
- A user's first message after the panel opened shows once: "Your messages and the data the assistant reads go to
  <provider>." (not for Ollama).

## Questions about OADM

`oadm_docs_search(query)`: the user docs of oadm.app (the pages under Get started, Devices, Sites, Device tasks, Plugins,
Administration; not the developer pages) ship with the server as a search index (`Assistant/DocsIndex`, built from
`website/src/content/docs` at build time into the server's resources). Answers title, page address on oadm.app and the
matching passage (at most 5 hits, 32 KB). A registry tool like every other, so MCP agents get it too.

## Settings (rail page **MCP & Assistant**, card **Assistant**, right of the card **MCP**)

Administrators only; changes audited ("Changed assistant").

- **Turn on** / **Turn off**.
- **Provider**: Anthropic, OpenAI, Ollama, or OpenAI-compatible (an address and a key: LM Studio, vLLM, Azure OpenAI).
- **API key** (not for Ollama): stored encrypted, shown as "Saved" afterwards, never sent back; **Address** for Ollama and
  OpenAI-compatible (`http://10.0.0.5:11434`). The saved key is used only for the provider and address it was saved
  with: another provider or address needs the key again ("Enter the API key." below the key), so a saved key never goes
  to a host just typed in.
- **Send pictures to the model**: a check box with its label on the left like every row, off by default; no hint.
- **Daily limit**: tokens a day for the whole server (default 2,000,000) and, optionally, per user; the panel shows
  the server's count of today next to the send arrow ("95,333 tokens today"; decision 2026-11-15: not on the card). At the limit the panel says "The assistant reached today's limit. It works again tomorrow." (reset at
  midnight server time).
- **Steps per answer** (1 to 50, default 20) and **Answer time (s)** (30 to 600, default 120; decision 2026-11-15): the
  model's tool calls per message and the longest one answer may take. 0 from an older client keeps the default. Audited.
- No model here (decision 2026-11-15): each user picks it in the panel.
- **Test**: checks the address and key by reading the provider's model list (Ollama, OpenAI-compatible without a list:
  one short call); "The provider answered." or the provider's error in plain words.
- No intro and no note under the provider on the card (decision 2026-11-15): the panel shows where the data goes
  (the notice of the first answer).
- Who may use it: permission **Use the assistant** (on for Administrator, off for the other built-in roles).

## The panel

- A title bar button (next to Help, icon chat) opens the panel **Assistant** on the right, over every page; it stays open
  while the user moves between pages. Hidden when the assistant is off or the role lacks the permission.
- Bottom: the message box like the chat apps, one rounded box (`Border.composer`, the input colours, the accent border
  while it has the focus): the chosen devices and the attached files as chips on top, the text (one line at first, grows to 200 px, then
  scrolls), and a row with **Attach file** (paperclip icon), **Choose devices** (devices icon), the model picker , the tokens of today (dim, right) and the round send arrow (`Button.send`,
  tooltip Send, dimmed while empty) that turns into a round **Stop** while an answer streams. While a preview card waits
  for Run, the send arrow is not the accent (`Button.send.quiet`): Run is the panel's one accent button. Below the box:
  its problem in red (the selection limit, "Choose a model.", the server's refusal of the model), a message ("Copied.",
  an upload's problem) and the selection count ("12 devices selected", nothing without a selection).
- Files dragged over the panel show "Drop files to attach" over it.
- Keys: Ctrl+J opens the panel with the focus in the message box and closes it while the focus is in it (the title bar
  button's tooltip says so), Enter sends, Shift+Enter a new line, @ chooses devices, Escape stops an answer that streams (focus in the panel), Up arrow in the
  empty box edits the last message.
- **Model** (decision 2026-11-15: chosen only in the panel, by each user): a picker in the box's row with the provider's
  models (`ListModels`; editable when the provider offers no list, e.g. some OpenAI-compatible servers). The choice is
  kept per user on that client (client settings); first time: the provider's first model, for Anthropic
  `claude-opus-5-5` when listed. Every message carries the model; changing it keeps the conversation. A model that
  cannot call tools answers once "This model cannot use tools. Choose another one."

## Limits

Per login session one conversation at a time (a new chat ends the one before), one answer and one tool call at a time;
the model's calls that act outside OADM (the network scan of Add devices) at most 10 a minute per session like an MCP
access key ("The assistant did this too often. Wait a minute."), camera pictures within the user's limit of every
channel ("Too many pictures. Wait a minute."); a Run is the user's click and counts against neither; the daily limit
counts each message's estimate at its start and settles it at its end; a Stop sent just before an answer began stops it;
OADM's own failure in a tool is a failed tool line ("The call failed. Try again."), never told as the provider's; at most **Steps per answer**
tool calls per message (default 20, then the model must answer without tools); tool answers at most 32 KB (the tools' own
limit); the model's context is cut from the oldest messages when it grows too long (older turns are dropped, and
within one message the older tool answers are shortened to 1,500 characters while the newest stays whole); a provider timeout of 120 s per
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
- The panel's second part (`AssistantPanelExtrasTests`): the counts follow single changes, cards per selection, page and
  role (at most 4), a Perf test at 100,000 devices (open, changes, links), @ and the chip, device links and their menu
  per role, Try the failed devices again, the run result line, CSV of both tables, Copy chat, Ctrl+J. Server:
  `RetryFailed` (only a card that ran, own chat, cut to the view) and `mentioned_device_ids` cut to the group.
- The chat-app panel (`AssistantChatUxTests`): Thinking until the first event, following only at the end and Go to the
  latest, Try again and Edit with `replace_last` and the old entries gone in one step, dropped and pasted files with the
  limit, Escape and Up arrow, and screenshots of the message box (empty, thinking, answer, streaming, scrolled up, drop,
  attachments). Server: `replace_last` drops the last exchange and needs one.

## Open points

- Voice: not planned.
