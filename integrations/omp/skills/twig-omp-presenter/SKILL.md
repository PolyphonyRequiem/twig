---
name: twig-omp-presenter
description: Present Twig proposal previews in OMP while preserving the exact digest, workspace, review model, and read-only brief/full switching.
---

# Twig OMP proposal presenter

Use alongside `twig`, not instead of it. This skill owns presentation only: it never approves, applies, or mutates a proposal, and it does not replace the canonical Twig change procedure.

## Use the retained preview

1. Resolve the intended Twig workspace and load the selected companion through the existing provider/scenario selection flow in `user-selections.json`.
2. Before any applicable human authorization, call the interactive OMP `twig_proposal_render({file,workspace?,full?})` tool once when it is available. The tool call owns the single native capture; do not run the CLI preview separately before or after it. Its native implementation reference is:

   ```sh
   twig proposal preview --file <absolute-file> -o json --include-rendering --width 100 --color always
   ```

   OMP requests `--color always` for the native frame. If `NO_COLOR` is set, request `--color never` instead so the host never receives ANSI it must reinterpret. The command above documents the tool's child-process request; it is not a second observation.

3. Retain the exact workspace, digest, `reviewModel`, and rendered presentation envelope from that single captured observation. The envelope is presentation version 1. Brief and full views must come from the same observation; details/back only switch between retained frames.
   The model receipt keeps the resolved file/workspace, native `digest`, `canApply`, `issues`, `pendingChanges` and `reviewModel`, with `displayed` set only after showing the frame and `approved: false`, `applied: false`.
4. The presenter is read-only. Closing it, moving back, or expanding details never authorizes or applies. The existing apply path and AFK delegation rules remain unchanged.
5. If the companion or tool is unavailable before a call, fall back to plain canonical review and disclose the gap; a direct CLI preview is allowed only for this deliberate pre-call fallback. If a call begins but returns a failed, truncated, unsupported, noninteractive or digest-mismatched response, stop the affected authorization, report the failure, and do not run a second preview, parse ANSI, or use a mutation fallback.

## Companion selection

- This skill is discovered from a conventional flat skills root. Keep it at `integrations/omp/skills/twig-omp-presenter/SKILL.md` in the source tree (and `skills/twig-omp-presenter/SKILL.md` in the packaged plugin) so OMP can load it by name.
- Link the OMP package through its normal plugin manager:

  ```sh
  omp plugin link /path/to/twig/integrations/omp
  # after extracting a local tarball:
  omp plugin link /path/to/unpacked/omp-proposal-presenter
  ```

  Use `link` for the source checkout or the extracted local package directory. This package is not currently published; use the local link flow only.
- After OMP has loaded the package, select its discovered companion through Twig without copying a second skill:

  ```sh
  twig skills configure --provider omp --target /path/to/twig-skills \
    --scenario terminal --companion twig-omp-presenter \
    --scan-root /path/to/twig/integrations/omp/skills
  ```

  For an extracted tarball, point `--scan-root` at its `skills` directory. Do not use `twig skills add` for this package: duplicate names can shadow rather than compose. Selection is separate from installation; verify OMP loaded the linked package and selected companion. For a source checkout whose `twig` executable is not on `PATH`, set `TWIG_BIN` to that executable for the plugin process; otherwise capture uses `twig` from `PATH`. Never hard-code a machine-specific path.

## Report faithfully

- Preserve the exact digest, workspace, and model in handoff text.
- Never reconstruct values from rendered text or a second preview.
- Use plain text when the selected companion is unavailable.
