---
command: web
group: context
summary: Open the active or specified work item in Azure DevOps in the default browser.
stability: stable
mutates: none
---

# `twig web`

Launches the default browser at the ADO work item edit URL for the
active item, or a specified ID. It never writes to ADO — the URL is
opened with `Process.Start` and the command prints a one‑line
confirmation.

## Synopsis

```
twig web [<id>] [--output <format>]
```

## Arguments

| Argument | Required | Description |
|---|---|---|
| `id` | no | Work item ID to open. Defaults to the active item selected with [`twig set`](./set.md). |

## Flags

| Flag | Type | Default | Description |
|---|---|---|---|
| `-h`, `--help` | flag | — | Show command help and exit. |
| `--version` | flag | — | Print the twig version and exit. |
| `-o`, `--output` | `human` \| `json` \| `minimal` | `human` | Output format for the confirmation record. |

## Behavior

1. **Target resolution.** If `id` is provided it is used verbatim.
   Otherwise the active work item is read from `IContextStore`; missing
   context exits `1` with `No active work item. Run 'twig set <id>' first.`
   (`src/Twig/Commands/WebCommand.cs:36-49`).
2. **Local seed guard.** A negative ID (i.e. an unpublished seed) exits
   `1` — the ADO edit URL only exists for published items
   (`src/Twig/Commands/WebCommand.cs:51-55`).
3. **Configuration check.** A missing `Organization` or `Project` in
   `TwigConfiguration` exits `1` with a hint to run `twig init`
   (`src/Twig/Commands/WebCommand.cs:57-61`).
4. **URL construction.** The URL is
   `https://dev.azure.com/{Organization}/{Project}/_workitems/edit/{id}`,
   with the org and project URL‑escaped
   (`src/Twig/Commands/WebCommand.cs:63`).
5. **Browser launch.** `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`
   dispatches to the OS default handler
   (`src/Twig/Commands/WebCommand.cs:65`). No wait, no capture — the
   command returns as soon as the process is started.
6. **Confirmation.** After launching, the item is read from cache to
   pick up its title (best‑effort), and a `browserOpened` record with
   `itemId`, `url`, `message`, and optional `title` is rendered
   (`src/Twig/Commands/WebCommand.cs:66-72`, `88-99`).

## Examples

```
$ twig set 1234
Set active item: #1234 Fix login redirect [Doing]

$ twig web
Opened #1234 Fix login redirect in browser.
```

```
$ twig web 1234 --output json
{"itemId":1234,"url":"https://dev.azure.com/contoso/Portal/_workitems/edit/1234",
 "title":"Fix login redirect","message":"Opened #1234 Fix login redirect in browser."}
```

## Exit codes and failure modes

| Condition | Result |
|---|---|
| Browser process started | `0` |
| No active work item and no ID given | `1` |
| Requested ID is a local seed (negative) | `1` |
| `Organization` or `Project` not configured | `1` |

## See also

- [`twig set`](./set.md) — pick the item that a bare `twig web` opens.
- [`twig show`](./show.md) — inspect the same item at the terminal.
- [`../getting-started/README.md`](../getting-started/README.md) — the
  `twig init` referenced when configuration is missing.
