# Twig commands

- Every invocation stands alone. The CLI neither selects a work item or Bench nor inherits a daemon view. Pass an explicit ID to `show` and `tree`; `show --local-id` reads a retained draft. Name the source with `--id` for single-item mutations.
- Seed creation requires `--parent <id>` or `--no-parent`. Seeds are local. Structured `seed publish` is unsupported; use a reviewed proposal.
- Use `twig bench list` to find a Bench, `twig bench rules <name>` to inspect it, and `twig workspace --bench <name>` to read it. Add `--refresh` for a supported named Bench pull; it does not publish drafts.
- Use `twig process <type>` and `twig process description <type-ref>` for native types, states, fields, and rules.
- Use `twig show <id> --refresh` for a fresh read and `twig pending -o json` for retained drafts.
