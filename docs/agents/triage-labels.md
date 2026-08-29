# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the
actual label strings used in this repo's issue tracker.

**The tracker is Azure DevOps** (`PolyphonyRequiem/Twig`), not GitHub — see
`docs/agents/issue-tracker.md`. ADO has no `label` concept; the equivalent is the **`System.Tags`**
field. So a "label" here is a **tag** on an ADO work item.

| Label in mattpocock/skills | Tag in our tracker | Meaning                                  |
| -------------------------- | ------------------ | ---------------------------------------- |
| `needs-triage`             | `needs-triage`     | Maintainer needs to evaluate this item   |
| `needs-info`               | `needs-info`       | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`  | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`  | Requires human implementation            |
| `wontfix`                  | `wontfix`          | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding
tag string from this table.

Edit the right-hand column to match whatever vocabulary you actually use.

## Applying a tag

`System.Tags` is a **semicolon-separated single string field**, not a list. There is no
add-one-tag verb, so a naive `twig update System.Tags "needs-triage"` **replaces every existing
tag**. Read the current value first, or use `--append`:

```bash
twig set <id>
twig show <id> --output json            # read System.Tags before you write it
twig update System.Tags "needs-triage" --append
twig sync                               # staged locally until pushed
```

🔴 **Removing a tag means rewriting the whole field.** Read it, drop the one you want gone,
write the remainder back without `--append`.

🔴 **Changes are staged until `twig sync` succeeds.** Do not report a tag as applied on the
board before then — a `PendingChangeRecord` lives only in the local SQLite cache.

## State is not a tag

ADO work items have a real **State** (`twig state <name>`), governed by the process's
`ProcessConfiguration`. Triage tags sit alongside it and do **not** move it.

🔴 Writing a work item's answer or close-gate fields does **not** move its State either — and
`twig state Done` cannot move it *for* them. `twig state` writes `System.State` alone, so on a
type whose process makes a field required in the Done state it refuses instead of emitting a
PATCH ADO would reject. Close by staging the transition and those fields in **one**
change-proposal `batch` op, applying it with
`twig proposal apply --confirm <digest> --authorize <identity>`, then re-reading with
`twig show <id> --refresh`.

## These tags may not exist yet

ADO creates a tag on first use, so applying one always "works" — which means a **typo silently
creates a new tag** rather than failing. That is this repo's false-green class again
(`AGENTS.md` § *The false-green class*). Copy the strings from the table above rather than
typing them, and check `twig show <id> --refresh` afterwards to confirm what actually landed.
