---
command: skills status
group: configuration
summary: Inspect installed guidance identity, integrity and bounded discovery.
stability: stable
mutates: none
---

# `twig skills status`

Inspect installed guidance identity, integrity and bounded discovery. See [skill delivery](../../features/skills.md) for provider setup,
ownership and recovery. No workspace, authentication, provider executable or
network is required; no provider settings or live profile are inferred.

## Synopsis

```text
twig skills status --provider hermes|omp|copilot --target PATH [--scan-root PATH] [-o human|json|minimal]
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

Reads only explicit target and optional scan-root paths. Reports absent, current or stale installation and selected companions. Edited/missing managed files are integrity failures. Missing selected companions and duplicate names produce warnings with a safe presentation fallback; required correctness and approval instructions remain mandatory. Does not create a missing target or repair files.

Discovery is bounded to direct child skill files, not all host profiles, project
ancestors, plugins or custom roots. Warnings do not prove a host loaded the
intended file. The provider adapter supplies setup guidance without executing
host commands or altering permissions. See the lifecycle result for every warning.

## Examples

```sh
twig skills status --provider copilot --target /path/to/skills
twig skills status --provider omp --target /path/to/skills --scan-root /path/to/companions -o json
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
