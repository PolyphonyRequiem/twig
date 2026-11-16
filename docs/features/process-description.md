# Process description

Twig discovers the connected provider's types, fields, states, transitions and
supported rules rather than assuming a process template.

```sh
twig process
twig process <discovered-type>
twig process description <discovered-type> -o json
```

`process description` projects the acquired Core schema with
`descriptorVersion: "core-schema-1"`, `scope: "provider-discovered-core-schema"`,
its schema fingerprint, and explicit `knownGaps`.
It is **not** the former complete ADO process/form-layout descriptor.

## Full and compact reads

```sh
twig process description -o json
twig process description <type-reference> -o json --sections requirements
twig process description <type-reference> -o json --sections fields --fields <field-reference>
```

Compact reads require an explicit type and JSON output. Valid sections are
`fields` and `requirements`. Unknown field references refuse instead of emitting
an apparently complete empty projection. Use the full route when omitted
evidence is needed.

`--out <path>` writes a completely rendered description to a file through a
temporary-file replacement. It is an explicit local side effect, not publication.
Use `-o json` for the complete structured projection.

## What the evidence does not prove

The projection excludes native form layout, display names/colors, category and
behavior metadata. Type/field identities are references. A fingerprint identifies
the acquired schema; it is not a server-wide revision or a guarantee about
external automation.

Global and type-local gaps remain visible. Missing relevant transition/rule
evidence blocks affected writes. Requirements are input for a transition
decision, not authorization to close an item by themselves.
GitHub process/workflow rule discovery remains unsupported even when native
API constraints are acquired.

`process layout` is retired. Human `states` is only a convenience for active
context; structured callers must use `process <type>` explicitly.
