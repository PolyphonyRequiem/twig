---
command: states
group: process
summary: Human-only hidden alias for the active Core item's discovered type.
stability: stable
mutates: none
---

# `twig states`

Human-only compatibility alias for `twig process <type>`. It resolves the active
item's type from the admitted Core context and SQLite mirror or local draft,
then discovers schema through the same Core workspace. A permanent local-to-remote
mapping is respected. No work-item query, refresh, sync or pending flush occurs.

```
twig states
```

An explicitly initialized Core workspace and cached active type are required.
Missing context or cached type exits 1 with a remedy rather than fetching work
implicitly. Type/state vocabulary comes from live discovery, not a process template.
`--org`/`--project` are not accepted on this alias.

For example:

```
twig set 1234
twig states
```

Structured output is refused with exit 2 before active context is read. Scripts
must choose a type explicitly:

```
twig process <discovered-type-reference> -o json
```

Success exits 0 and renders the same type projection as the explicit command.
Discovery/admission failures exit 1; there is no local or remote mutation.

## See also

- [`twig process`](process.md)
- [`twig process <type>`](process-type.md)
- [`twig process description`](process-description.md)
