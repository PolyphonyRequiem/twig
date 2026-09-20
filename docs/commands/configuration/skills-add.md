---
command: skills add
group: configuration
summary: Install one separately supplied integration or variant skill.
stability: stable
mutates: local
---

# `twig skills add`

Install a local, separately supplied skill into an explicit provider target.
Nothing is downloaded or executed. This is the later-installation path for
integration/variant skills; those packages are not bundled Twig defaults.

## Synopsis

```text
twig skills add --provider hermes|omp|copilot --target PATH --source SKILL_DIRECTORY [--scan-root PATH] [-o human|json|minimal]
```

## Flags

|Flag|Type|Default|Description|
|---|---|---|---|
|`--provider`|string|required|Explicit provider matching the target's manifest.|
|`--target`|string|required|Root containing an installed canonical Twig skill.|
|`--source`|string|required|One local skill directory containing SKILL.md and support files.|
|`--scan-root`|string|null|Additional bounded discovery root; not registered automatically.|
|`-o`, `--output`|string|human|Human, JSON or minimal output.|
|`-h`, `--help`|flag|false|Read help without executing the operation.|

## Behavior

The skill must have its own portable name, distinct from canonical `twig` and the
retired reserved names `twig-cli` and `twig-changes`. Review the package before
installation: this command copies content, not its authority, and never executes supporting scripts. Existing
conflicting destinations and unsafe paths are refused rather than overwritten.
An identical reinstall is idempotent; different bytes or Unix executable modes
are a conflict, not an implicit third-party upgrade. Preserve the existing
directory and choose a separately named version or new root for a changed package.

Provider discovery is bounded to direct child skills in the explicit roots.
Duplicate names are reported as shadowing, not claimed as composition. The
command does not register custom roots, modify host settings or grant tool
permissions. Base updates preserve separately installed skill bodies and user
settings. Use `skills configure` to select a companion for a scenario.

Installation and bounded discovery share a semantic YAML parser. Quoted keys
and multiline descriptions are supported; duplicate/merge keys, anchors,
aliases, explicit tags and non-scalar names/descriptions are rejected. Source
metadata requires one string name and a nonempty description. Quote implicit
YAML values such as `"on"` when using them as names.

Unix installs preserve ordinary file permissions, including executable bits,
but strip setuid/setgid/sticky bits. Re-add checks executable modes as well as
byte hashes; status reports mode drift as an external warning, while its
top-level state describes the canonical package. Base updates preserve external
edits. Legacy/Windows manifests without a Unix mode baseline require a fresh
target to establish tracking from trusted source. Windows copies bytes and
reports that Unix execution modes are not verified; no Windows ACL or
interpreter installation is performed.

## Examples

```sh
twig skills add --provider omp --target /path/to/skills --source /path/to/my-presenter
twig skills add --provider copilot --target /path/to/skills --source /path/to/my-integration -o json
```

## Exit codes and failure modes

|Condition|Result|
|---|---|
|Installed or identical package already present|`0`; inspect discovery warnings|
|Missing/invalid source, unsafe path, conflict, edited destination or I/O failure|`1`; no force overwrite|
|Missing required option or invalid CLI syntax|Nonzero parser usage failure|

No live host loading is claimed by installation. After an interrupted I/O
failure preserve the target and inspect status; do not assume rollback.

## See also

- [Skill delivery](../../features/skills.md)
- [Companion selection](skills-configure.md)
