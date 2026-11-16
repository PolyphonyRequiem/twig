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

Read the current tags and the bound type's field definition before writing.
The ADO wire representation is semicolon-separated text; use the **typed Core
value** admitted by the discovered schema rather than assuming a wire string
or a generic append operation. Preserve all unrelated tags.

```bash
twig show 1234 --refresh -o json
twig process description "<discovered-type-reference>" -o json
twig update --help
```

**Removing a tag means replacing the field with the retained values.** Do not
silently replace every tag with the one being added.

For a published target, `update --id <id>` stages, previews and saves through Core
immediately. It is not the old pending-writer path and does not need a later
`sync` to push. Inspect the returned outcome and refresh-read the affected item
before reporting verified tracker state. Local seed edits remain local.

## State is not a tag

ADO work items have a real **State** (`twig state <name>`), governed by the process's
discovered provider schema. Triage tags sit alongside it and do **not** move it.

🔴 Writing a work item's answer or close-gate fields does **not** move its State either — and
`twig state Done` cannot move it *for* them. `twig state` writes `System.State` alone, so on a
type whose process makes a field required in the Done-category state it cannot
supply that field automatically. Close by staging the transition and those fields in **one**
change-proposal `batch` op, applying it with
`twig proposal apply --file <path> --confirm <digest>`, then re-reading with
`twig show <id> --refresh`.

## These tags may not exist yet

ADO creates a tag on first use, so applying one always "works" — which means a **typo silently
creates a new tag** rather than failing. That is this repo's false-green class again
(`docs/incidents/false-green-class.md`). Copy the strings from the table above rather than
typing them, and check `twig show <id> --refresh` afterwards to confirm what actually landed.
