---
command: skills configure
group: configuration
summary: Select or clear an additive companion for one provider and scenario.
stability: stable
mutates: local
---

# `twig skills configure`

Select or clear an additive companion for one provider and scenario. See [skill delivery](../../features/skills.md) for provider setup,
ownership and recovery. No workspace, authentication, provider executable or
network is required; no provider settings or live profile are inferred.

## Synopsis

```text
twig skills configure --provider hermes|omp|copilot --target PATH --scenario NAME (--companion NAME | --clear) [--scan-root PATH] [-o human|json|minimal]
```

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
|`--provider`|string|required|Explicit provider; a target manifest pins this value.|
|`--target`|string|required|Skills root containing flat sibling skill directories.|
|`--scan-root`|string|null|One additional bounded discovery root; never registered automatically.|
|`--scenario`|string|required|Scenario name: lowercase letters, digits and internal hyphens.|
|`--companion`|string|null|Separately named skill; exclusive with --clear.|
|`--clear`|flag|false|Remove this scenario selection.|
|`-o`, `--output`|string|human|Human, complete JSON, or pipe-friendly minimal output.|
|`-h`, `--help`|flag|false|Offline help without executing the operation.|

## Behavior

Requires a current, unedited base installation. Select exactly one separately named companion with --companion, or remove the selection with --clear. A new selection must resolve uniquely in the target and optional scan root; a missing or shadowed companion is refused before a write. Writes only the selection reference and manifest, preserving other scenarios and user-authored content.

Discovery is bounded to direct child skill files, not all host profiles, project
ancestors, plugins or custom roots. Warnings do not prove a host loaded the
intended file. The provider adapter supplies setup guidance without executing
host commands or altering permissions. See the lifecycle result for every warning.

## Examples

```sh
twig skills configure --provider omp --target /path/to/skills --scenario terminal --companion my-presenter
twig skills configure --provider omp --target /path/to/skills --scenario terminal --clear -o json
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
