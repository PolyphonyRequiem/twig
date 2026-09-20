---
command: skills update
group: configuration
summary: Update unedited Twig-owned guidance while preserving companions.
stability: stable
mutates: local
---

# `twig skills update`

Update unedited Twig-owned guidance while preserving companions. See [skill delivery](../../features/skills.md) for provider setup,
ownership and recovery. No workspace, authentication, provider executable or
network is required; no provider settings or live profile are inferred.

## Synopsis

```text
twig skills update --provider hermes|omp|copilot --target PATH [--scan-root PATH] [-o human|json|minimal]
```

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
|`--provider`|string|required|Explicit provider; a target manifest pins this value.|
|`--target`|string|required|Skills root containing flat sibling skill directories.|
|`--scan-root`|string|null|One additional bounded discovery root; never registered automatically.|
|`-o`, `--output`|string|human|Human, complete JSON, or pipe-friendly minimal output.|
|`-h`, `--help`|flag|false|Offline help without executing the operation.|

## Behavior

Requires an existing installation pinned to the same provider. Verifies all manifest-owned bytes before changing them; edited or missing managed content refuses the update. The supported `twig-cli`/`twig-changes` package migrates to the single `twig` skill, moving its selection file while preserving selections, separately named companions and unrelated settings. A conflicting unmanaged `twig` destination is refused. A matching identity is a no-op.

Discovery is bounded to direct child skill files, not all host profiles, project
ancestors, plugins or custom roots. Warnings do not prove a host loaded the
intended file. The provider adapter supplies setup guidance without executing
host commands or altering permissions. See the lifecycle result for every warning.

## Examples

```sh
twig skills update --provider omp --target /path/to/skills
twig skills update --provider hermes --target /path/to/skills --scan-root /path/to/companions -o json
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Operation completed; inspect warnings and reported state|`0`|
|Integrity/conflict/provider/path/I/O failure|`1`; no force overwrite|
|Missing required CLI option or invalid syntax|Nonzero parser usage failure; no operation executes|

Writes use cooperating-process locking and per-file atomic replacement, not a
multi-file transaction. Preserve the target after an interrupted I/O failure;
do not assume rollback. `status` itself never writes.
