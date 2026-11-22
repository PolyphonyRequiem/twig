---
command: version
group: system
summary: Print the installed CLI version offline.
stability: stable
mutates: none
---

# `twig version`

`twig --version` also prints the installed CLI version.

## Behavior

Offline version discovery without a workspace, sign-in or update check.
It does not describe the separately versioned server/canvas runtime.

## Effects

None beyond output.

## Failure modes

Unsupported operating systems refuse. Version text alone is not proof of a
package's integrity or provider qualification.
