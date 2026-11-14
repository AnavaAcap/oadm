---
title: Plugin tools
description: "Offer steps of your core plugin to AI agents and other programs: the tool list, previews and runs, paged lists."
---

A **tool** is one step of your core plugin that a program can take without the plugin's page: read the certificate list,
create a certificate. OADM lists your tools while the plugin is on and the caller's role has the permission. A tool is not
tied to one channel: AI agents use it today, a built-in assistant can use it later (SDK 0.13.0).

## Declare the tools

```csharp
static readonly JsonSerializerOptions Json = PluginTool.CreateJsonOptions();

public IReadOnlyList<PluginTool> Tools { get; } =
[
    new("gates", "Gates", "Lists the gates and whether they are open.")
    {
        InputType = typeof(GatesArgs),
        OutputType = typeof(GatesAnswer),
        JsonOptions = Json,
        Paged = true,
    },
];
```

The host names it `oadm_<area>_<name>`: the area is the plugin id without `oadm.` (`ToolArea` changes it), the name is yours.
Use lower case words with underscores, at most 64 characters in all. A tool that does not fit is left out and logged.

- `InputType` is the record of the arguments. The host writes its JSON Schema like it does for
  [task payloads](../tasks/#tasks-for-ai-agents): `[Description]` explains a property, `[MaxLength]` and `[Range]` bound it,
  nothing else is accepted. A `[Secret]` property keeps the tool out. `InputSchema` takes the schema as text instead.
- `OutputType` is the record of the answer, an object. A text from a device or a user goes only in a value, never in a
  description.
- `Description`: one to three short sentences. The host checks every call against the schema before your code runs, and an
  answer larger than 32 KB is refused, so keep rows short.

## Handle the call

A tool call arrives in your own `InvokeAsync(ICallContext caller, ...)` as the method `tool.<name>`. It is the same path
as your page, so `RequiredRole`, `RequiredPermission`, `RequiresAllDevices`, `IsAudited` and the device view of `caller`
apply. Give the tool methods the rules of the page method that does the same.

```csharp
if (method == "tool.gates")
{
    var page = PluginToolPage.Of(payloadJson);
    var (rows, more) = page.Take(gates.Where(g => page.Includes(g.Id)).Select(Row), Json);
    return JsonSerializer.Serialize(new GatesAnswer(gates.Count, rows, more ? rows[^1].Id : null), Json);
}
```

- A refusal of the arguments: `PluginToolErrors.Answer([new("name", "Enter a name.")])`, one entry per field, in the words
  of your page. Any other failure: throw `InvalidOperationException` with the text to show.
- Never put a password, key or token in an answer.

## Lists

Set `Paged` and read the page with `PluginToolPage.Of(payloadJson)`: `Limit` and `After`, the sort key of the last row
before. Return the rows in the order of that key, `Take` stops at the limit and at about 24 KB, and your answer has `total`
and `next`, the key of the last row (null at the end). The host adds `limit` and `cursor` to the input, signs `next` into
`next_cursor`, and refuses a cursor of another caller or another search.

## Changes

Set `Changes` and the host makes two tools, `<name>_preview` and `<name>_run`. Implement two methods:

- `tool.<name>.preview` checks everything the page checks and answers the plan. It never writes. The host adds a `handle`
  and `expires`.
- `tool.<name>` does the change. The run tool takes only the handle of a preview, once, within 10 minutes, and calls it
  with the same arguments, so check everything again. Give `RunOutputType` for its answer.

Set `Destructive` for a change that removes something. A Read only caller gets the previews and never the runs.

## Test

Call your `InvokeAsync` with `FakeCallContext` for a user with all devices and for a group: the answer, the field errors,
that a preview leaves nothing behind, and that no answer holds a secret.
