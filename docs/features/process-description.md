# Process/schema description

Use `twig process` and `twig process description <discovered-type-reference>`
to inspect the bound provider's current schema and requirements. Types, fields,
states, transitions, allowed values and gaps are discovered, never inferred
from a named ADO template.

Machine reads should use structured output and select the needed sections/fields
through current help. Preserve missing/unknown distinctions and follow a full
read hint when projected facts are insufficient. A cached description is scoped
to its source and capture; it is not fresh permission to transition an item.

For close gates, inspect requirements for the exact target type and desired
state. Include required fields with no supplying rule in the same batch
operation as the transition. Unavailable rules or incomplete relevant schema
must not be treated as no requirements.

The old Domain whole-process/layout descriptor and standalone MCP tool are not
current contracts. See the [command reference](../commands/process/README.md)
and [Core boundary](../architecture/clean-core-contract.md).
