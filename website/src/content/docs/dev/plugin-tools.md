---
title: Plugin tools
description: "Offer steps of your core plugin to AI agents and other programs: the tool list, previews and runs, paged lists."
---

A **tool** is one step of your core plugin that a program can take without the plugin's page: read the certificate list,
create a certificate. OADM lists your tools while the plugin is on and the caller's role has the permission. A tool is not
tied to one channel: AI agents over MCP and the Assistant in OADM use the same tools (SDK 0.13.0).

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

The host names it `oadm_<area>_<name>`: the area is the plugin id without `oadm.` (`ToolArea` changes it, `PluginTool.Area`
changes it for one tool), the name is yours.
Use lower case words with underscores, at most 40 characters for your name (`PluginTool.MaxNameLength`). A tool with a
longer name is left out and logged.

- `InputType` is the record of the arguments. The host writes its JSON Schema like it does for
  [task payloads](../tasks/#tasks-for-ai-agents): `[Description]` explains a property, `[MaxLength]` and `[Range]` bound it,
  nothing else is accepted. A `[Secret]` property keeps the tool out (a change asks for a secret through its plan, see
  [Secrets](#secrets)). `InputSchema` takes the schema as text instead.
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

## Tools on devices

Set `DeviceFilter` for a tool that works on devices the caller chooses. The host adds `filter`, the same device filter as
its own device tools (ids, search, tags, sites, models, AXIS OS, status, the assistant's selection), finds the matching
devices of the caller's view and hands your method their ids instead, at most `PluginTool.MaxDevices` (500), sorted by
address:

```csharp
var devices = PluginToolDevices.Of(payloadJson); // Ids, Total, Left
foreach (var id in devices.Ids)
{
    if (await caller.Devices.FindAsync(id, ct) is not { } device)
    {
        continue; // no longer there or outside the caller's devices
    }
}
```

Your input type has no property `filter`, `device_ids` or `devices_total`. Read the devices with bounded parallelism and a
time budget, and answer what you read with a count of the devices you did not read, so a call over many devices never
hangs the caller.

Set `TaskPlugin` to the id of a task of your package when the tool reads what that task's dialog reads: the host then lists
the tool only for a role with one of the task's permissions. A page call reaches your method without the host's list, so
check `caller.Has(...)` again there.

## Changes

Set `Changes` and the host makes two tools, `<name>_preview` and `<name>_run`. Implement two methods:

- `tool.<name>.preview` checks everything the page checks and answers the plan. It never writes. The host adds a `handle`
  and `expires`.
- `tool.<name>` does the change. The run tool takes only the handle of a preview, once, within 10 minutes, and calls it
  with the same arguments, so check everything again. Give `RunOutputType` for its answer.

Set `Destructive` for a change that removes something.

## Page actions

What a user does on your page, an agent can do too. `PluginPageTools<TOwner>` wraps a method of your page as a tool, with a
typed input and the page's own checks; it never calls a page method you did not declare:

```csharp
internal static class GateTools
{
    public static PluginPageTools<GatesPlugin> Set { get; } = new PluginPageTools<GatesPlugin>()
        .Read<NoArgs, GatesAnswer>(new("gates", "Gates", "Lists the gates."), "list",
            (plugin, caller, input, ct) => plugin.ListAsync(caller, ct))
        .Change<SaveArgs, SavePlan, Saved>(new("gate_save", "Save gate", "Adds or changes a gate."), "save",
            (plugin, caller, input, ct) => plugin.CheckSaveAsync(caller, input.Args, ct), // the page's validation, never writes
            (plugin, caller, input, plan, ct) => plugin.SaveAsync(caller, plan, ct));     // the page's Save
}

internal sealed record SavePlan(string Name) : PluginToolPlan(1, "gate"); // the card says "1 gate"
```

- `Read` is one tool: a read, or a start your page does without asking (a scan, a check, a read of the devices). `Change`
  is a preview and a run: the preview calls the check, the run calls the check again and then your page's code.
- In the check, throw `PluginToolInputException("name", "Enter a name.")` for a field, or `InvalidOperationException` with
  the page's text for another refusal.
- `input.Devices` holds the devices of a tool with `DeviceFilter`, `input.Page` the page of a `Paged` list.
- Hand `Set.Tools` to `Tools`, answer `Set.Handles(method)` with `Set.InvokeAsync(this, caller, method, payloadJson, ct)`,
  and read every tool method as its page method in your rules, so the tool has exactly the page's rights:

```csharp
public string? RequiredPermission(string method) => GateTools.Set.RulesMethodOf(method) is "save" ? PermissionKeys.Manage : PermissionKeys.Show;
public bool IsAudited(string method) => !PluginTool.IsPreviewMethod(method) && GateTools.Set.RulesMethodOf(method) is "save";
```
## Secrets

A change that needs a password, a passphrase or a key never takes it as an argument. Declare it on the tool; the person
enters it in their own shell or in the assistant's card, never through the model:

```csharp
private static readonly PluginToolSecret Password = new("password", "password");

.Change<SaveArgs, SavePlan, Saved>(new("gate_save", "Save gate", "Adds or changes a gate.") { Secrets = [Password] }, "save",
    (plugin, caller, input, ct) => input.Secret("password") is { Length: < 8 }
        ? throw new PluginToolInputException("secrets.password", "Use at least 8 characters.") // checked when entered
        : plugin.CheckSaveAsync(caller, input.Args, ct),
    (plugin, caller, input, plan, ct) => plugin.SaveAsync(caller, plan, input.Secret("password")!, ct))
```

- The run needs every declared secret, unless the plan says otherwise in `SecretsNeeded`: `[]` when this run needs none (a
  switch whose secret stays), some of the declared ones, or secrets the plan makes itself (one per password field of a
  saved command: `new PluginToolSecret("password_1", "password of Set user")`). At most 8.
- The preview has no value (`input.Secret` is null). When the person enters one, the host calls the check with that value
  alone; a field error at `secrets.<name>` refuses it and the person corrects it. The run gets every value, with
  `input.IsRun` true.
- A tool without `PluginPageTools` puts `secrets_needed` (`PluginToolSecrets.Property`, a list of `PluginToolSecret`) into
  its preview's answer and reads the values with `PluginToolSecrets.Of(payloadJson)`.
- Never put a value into an answer, an audit detail, a log line or a stored setting in clear (`ctx.Secrets`).

## Files

A tool that hands the caller a file (an export, a report, a certificate) sets `ReturnsFile` and puts a `PluginToolFile`
into its answer's `file` property: one of your downloads (`ICorePluginDownloads.OpenDownloadAsync`) with its payload.

```csharp
return new ExportAnswer(Recordings: 1, File: new PluginToolFile("export", JsonSerializer.Serialize(request, Json), "recording.mp4", "video/mp4"));
```

The host never passes the bytes through the answer. It replaces `file` with a file reference; an agent turns it into a
one-time download link, the assistant shows a card with Save. The bytes are read only then, through your download as the
caller: its permission, device group and audit apply again. Give `Size` only when you know it exactly. Never a backup, a
private key or a PKCS#12 file, except a key backup protected with a password the person enters as a secret.

A tool that takes a file has a `file_id` argument: the caller uploads the file first (an agent's upload link, the
assistant's Attach file). Read it with `caller.Uploads.FindAsync` and `OpenReadAsync`; another user's upload answers like
an unknown id. In tests set `FakeCallContext.Uploads` to a `FakeUploadedFiles` and `Add` the file.

## Your duty

The host trusts what you declare. A tool without `Changes`, and every `tool.<name>.preview`, is announced as a read and
offered with only the permission you name, so it must never change a device, a setting or anything a user saved. The one
exception is a start your page does without asking (a scan, a check, a read of the devices): it may refresh what the page
shows. Anything else that writes is a change with a preview and a run. Never return a password, private key, license key or token, and keep texts from devices and users in their own
fields, never in a message.

## Test

Call your `InvokeAsync` with `FakeCallContext` for a user with all devices and for a group: the answer, the field errors,
that a tool without `Changes` and every preview leave nothing behind, and that no answer holds a secret.
