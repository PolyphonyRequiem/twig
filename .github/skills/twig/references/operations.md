# Twig commands

- Structured single-item mutations require `--id`. Targeted `show` and `tree` require an explicit ID; `show --local-id` reads a retained draft.
- Structured seed creation requires `--parent <id>` or `--no-parent`. Seeds are local. Structured `set`, navigation, and `seed publish` are unsupported.
- Use `twig process <type>` and `twig process description <type-ref>` for native types, states, fields, and rules.
- Use `twig show <id> --refresh` for a fresh read and `twig pending -o json` for retained drafts.
