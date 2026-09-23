# Installed help and Twig skills

Twig users and agent operators can read the installed command contract without a
checkout, authentication, workspace, Python or network. Start with `twig --help`,
select a group with `twig proposal --help`, and then read a leaf such as
`twig proposal apply --help`. `twig --help-all` retains complete discovery.
Syntax and defaults come from command declarations; behavioral sections come
from the embedded canonical command reference, not a second command catalog.

## Generic guidance, explicit installation

`twig --skill` prints the executable-matched canonical **twig** foundation and
package identity. Its single maintained source is `.github/skills/twig/SKILL.md`;
the executable embeds that file and its conditional `operations`, `changes`, and
`presentation` references. Read those offline from an executable-only environment
with `twig --skill operations|changes|presentation`. Provider integrations,
process-specific workflows and presentation variants are not bundled defaults.

Every lifecycle command requires an explicit provider and skills-root target.
A target is the directory containing flat sibling skill directories, not the
`SKILL.md` file or the directory of an individual skill. Choose the intended
user, repository or named-profile root yourself; Twig neither infers a home
profile nor edits host settings. An absent provider executable does not prevent
preparing a custom target for later use.

```sh
twig skills install --provider omp --target /path/to/skills
twig skills status --provider omp --target /path/to/skills --output json
twig skills update --provider omp --target /path/to/skills
```

The identity includes the full assembly version/build and a content digest.
`status` reports `stale` when that identity differs. `install` is idempotent only
for a matching unedited installation; `update` is the explicit version-change
path. Neither ordinary Twig use nor `--skill` modifies installed guidance.

## OMP proposal presenter companion

The first-party OMP package exposes the `twig-omp-presenter` companion from its conventional `skills/twig-omp-presenter/SKILL.md` directory (the source checkout path is `integrations/omp/skills/twig-omp-presenter/SKILL.md`). Link the real package through OMP's normal plugin manager; for a local tarball, extract it first and link the package directory. This package is not currently published; use the local link flow only.

```sh
omp plugin link /path/to/twig/integrations/omp
# after extracting a local tarball:
omp plugin link /path/to/unpacked/omp-proposal-presenter
```

Use the first form for a source checkout and the second for an extracted local package directory. Both are ordinary OMP plugin-manager link operations; no npm availability is implied.

After OMP has loaded the package, select its package-discovered companion without copying a second skill:

```sh
twig skills configure --provider omp --target /path/to/twig-skills \
  --scenario terminal --companion twig-omp-presenter \
  --scan-root /path/to/twig/integrations/omp/skills
```

For an extracted tarball, point `--scan-root` at its `skills` directory. Do not use `twig skills add` for this package: duplicate names can shadow rather than compose. `--scan-root` inspects the package's conventional flat skills directory; OMP plugin discovery remains the loading mechanism. Selection is separate from installation, and Twig does not rewrite host settings. Confirm that OMP loaded the linked package and selected companion before using retained-observation review.

## Compact consumer guidance

Compact projections are opt-in, not a replacement for the full route. Preserve target identity, freshness, completeness, and errors, and keep a documented full-output path available whenever the caller does not ask for projection. Bind the executable identity (absolute path plus digest), workspace, and organization/project explicitly before comparing compact and full reads. Help reuse is version-keyed to that executable identity, so re-read or reinstall when the executable changes. Never parse truncated presentation text as JSON; parse the command's actual machine output.


## Provider discovery

The provider adapter reports setup guidance after an operation. Installation is
not proof that a host trusts, enables or loads the files. Register custom roots
and verify the actual loaded skill in the intended host. The root's manifest
pins one provider; use a distinct root for another provider.

|Provider|Explicit root choice and discovery|
|---|---|
|Hermes|Choose the intended profile's `skills` root or configure `skills.external_dirs` yourself. Project `.hermes/skills` and `.agents/skills` require project trust.|
|OMP|Use flat sibling directories in a native skills root or register the root through `skills.customDirectories`. Nested categories are not discovered automatically.|
|GitHub Copilot CLI|Use repository `.github/skills` or `.agents/skills`, personal `.copilot/skills` or `.agents/skills`, or an explicitly registered custom root. Reload skills and inspect their locations.|

`--scan-root PATH` adds one explicit root to the integrity/discovery inspection.
It does not register that root or prove global precedence. Checks are bounded
to direct child `SKILL.md` files; other profiles, project ancestors, plugins,
custom roots, trust and enablement are not globally scanned. Duplicate names
are potential shadowing, not composition. Resolve them and inspect the actual
host-loaded file before relying on a companion.

Retrieved provider contracts: [Hermes skills](https://hermes-agent.nousresearch.com/docs/user-guide/features/skills),
[OMP 18.1.16 skills](https://github.com/can1357/oh-my-pi/blob/v18.1.16/docs/skills.md),
and [Copilot CLI skills](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/add-skills).
These conventions are not a guarantee that every future provider version uses
identical discovery or precedence.

## Separately supplied companions

Use a unique name for a user skill; a same-name override replaces base guidance
rather than augmenting it. `skills add` copies a separately supplied local skill
directory and its support files without executing scripts or fetching content.
Review that source first; an integration skill may contain powerful instructions.
An identical reinstall is idempotent; changed bytes or Unix executable modes
are a conflict, not a silent third-party upgrade. Preserve existing files and
use a separately named version or fresh root for changed packages.

```sh
twig skills add --provider omp --target /path/to/skills \
  --source /path/to/my-twig-presenter
```

Install and discovery use the same YAML parser. Quoted keys and quoted or
multiline descriptions are supported. Duplicate keys (including escaped or
quoted spellings), merge keys, anchors, aliases, explicit tags and non-scalar
names/descriptions are rejected instead of guessing a provider's interpretation.
The skill must have one string name; quote names that YAML implicitly types
(for example `"on"`). Source installation also requires a nonempty description.

On Unix, ordinary file permissions are preserved, including executable bits;
setuid, setgid and sticky bits are stripped. Re-add refuses executable-mode
drift even when bytes match, and status warns without blocking base updates.
The top-level `current` state describes the canonical package, so also inspect
external warnings. Legacy or Windows-created manifests have no Unix mode
baseline: use a fresh target and trusted source to establish one. Windows
installs copy bytes but do not preserve or verify Unix modes; executable
behavior depends on Windows permissions and the installed interpreter.

Select the installed skill for a provider/scenario without rewriting its content:

```sh
twig skills configure --provider omp --target /path/to/skills \
  --scenario terminal --companion my-twig-presenter
twig skills configure --provider omp --target /path/to/skills \
  --scenario terminal --clear
```

Selections live in the Twig-managed `twig/references/user-selections.json`.
The foundation tells the agent to read this reference and load the selected
companion with its native reader. This is instruction-driven composition, not
cross-host inheritance or permission enforcement. Updates preserve selections,
companion content and unrelated provider settings.

If a selected optional presenter disappears, status reports that condition and
the generic skill requires a visible presentation fallback. Missing required
correctness or approval instructions still block the affected operation. A
companion cannot hide material effects, failed or untouched work, invent facts,
or grant authority. The no-agent human/JSON command surfaces remain available.

## Ownership and recovery

Twig refuses an existing family directory without its ownership manifest, edited
managed files, conflicting unmanaged destinations, provider mismatch and unsafe
paths. There is no force-overwrite switch. Preserve custom edits as a separately
named companion, restore the matching original bytes from a trusted backup, or
install to a new target. Unrelated user files are never a cleanup target.

Symlink/reparse-point paths and portable path aliases are rejected. Cooperating
operations use a local lock, per-file writes use atomic replacement, and the
manifest is written last. A multi-file update is not transactionally atomic:
I/O interruption can leave an inconsistent installation. Inspect `status`,
preserve the target, and restore from a trusted backup or choose a fresh root.
Do not infer rollback from a nonzero exit. Concurrent hostile filesystem writers
are outside this user-owned-directory utility's trust boundary.

## Verification boundaries

Automated provider fixtures and compiled/published probes exercise explicit
roots, integrity, preservation, conflicts and discovery warnings. They do not
establish model obedience, Discord rendering, interactive approval capture or
live-profile loading. Linux path tests do not establish Windows/macOS runtime
compatibility; native provider loading is reported separately when exercised.
The offline lifecycle intentionally avoids telemetry/network startup and needs
no MCP tool: it configures local installation rather than tracker operations.
