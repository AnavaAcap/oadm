# Assistant (chat with an AI inside OADM)

Status: **spec, not built** (decisions of 2026-11-15).

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
   it (the run tool with the preview's handle). The model never calls a run tool.
3. **No secrets.** The provider's API key is stored encrypted with the master key (like every secret), never sent to a
   client, never logged. No tool takes or returns a password, key or token (`docs/spec/mcp.md` rule 4).
4. **Device texts are data.** Names, tags, event texts, log lines and tool answers go to the model as data, never as
   instructions (the system prompt says so, like the skill); a text that tries to give an order never changes what a run
   does, because every run is the user's click on a preview.
5. **Nothing is kept.** A chat lives only while its panel is open; closing it or signing out forgets it. The server keeps
   no chat text. The audit log keeps every change as always.
6. **Off by default**, and only administrators set it up.

## Where it runs

- **Server**: the conversation loop (`Microsoft.Extensions.AI` `IChatClient`): the user's message, the tool list of the
  user (the registry's list for that caller, preview and read tools only; run tools are never offered to the model), the
  model's answer, tool calls run in process, until the model answers in text. One conversation per open panel, in server
  memory only, ended when the panel closes, the session ends or after 30 minutes without a message.
- **Client**: the panel. A new gRPC service `AssistantService` (`Chat` server-streaming: the answer's text as it comes,
  tool calls as short lines "Looking at devices…", preview cards; `Run(handle)`; `End`). Run goes through the same tool
  registry (the run tool of the preview), not through the model.

## Settings (card **AI**, section **Assistant**, below the section **MCP**)

Administrators only; changes audited.

- **Provider**: Anthropic, OpenAI, Ollama, or OpenAI-compatible (an address and a key: LM Studio, vLLM, Azure OpenAI).
- **API key** (not for Ollama): stored encrypted, shown as "Saved" afterwards, never sent back; **Address** for Ollama and
  OpenAI-compatible (`http://10.0.0.5:11434`).
- **Model**: the model name, with the provider's list when it offers one.
- **Daily limit**: tokens a day for the whole server (default 2,000,000) and, optionally, per user; **Used today** shows
  the count. At the limit the panel says "The assistant reached today's limit. It works again tomorrow." (reset at
  midnight server time).
- **Test**: one short call to the provider, the answer or the provider's error in plain words.
- A note under the provider for the cloud ones: device names, addresses and other data of your devices go to that
  provider. Ollama keeps everything in your network.
- Who may use it: permission **Use the assistant** (on for Administrator, off for the other built-in roles).

## The panel

- A title bar button (next to Help, icon chat) opens the panel **Assistant** on the right, over every page; it stays open
  while the user moves between pages. Hidden when the assistant is off or the role lacks the permission.
- Messages, the model's answers as they stream, short tool lines, preview cards with **Run** (and **Cancel**), the run's
  result (task counts, a link to the tasks). **New chat** clears it.
- A user's first message after the panel opened shows once: "Your messages and the data the assistant reads go to
  <provider>." (not for Ollama).

## Limits

Per user one conversation at a time; at most 20 tool calls per message; tool answers at most 32 KB (the tools' own
limit); the model's context is cut from the oldest messages when it grows too long; a provider timeout of 120 s per
answer ("The provider did not answer. Try again.").

## Tests

- Fake `IChatClient` that asks for given tool calls: rights of a group user apply; a role without a permission gets no
  tool for it; run tools are never offered to the model; Run only from the user's call with a valid handle.
- No secret in any answer, log line or audit entry (the scan of the MCP tests over the assistant's traffic).
- The daily limit with a `FakeTimeProvider` (reset at midnight), per server and per user.
- Headless panel tests with screenshots; the settings card's validation below the fields.

## Open points

- Choosing the model per user, voice, files dropped into the chat: not planned.
