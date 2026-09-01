# Process commands

Commands that let you inspect the Azure DevOps process backing a project: the
work item types, their states, their fields, and the form layouts and
structural descriptions ADO serves for them. Every value these commands report
is **discovered dynamically** from the process description (via
`IProcessConfigurationProvider`, `IProcessTypeStore`, and the layout provider).
Nothing here hard-codes a state name, a type name, a category, or a field —
they are read from the live process (or, where noted, the workspace cache).

|Command|Summary|
|---|---|
|[`twig process`](./process.md)|List dynamically discovered visible work-item types.|
|[`twig process <type>`](./process-type.md)|Describe one type's dynamically discovered states, fields, and transitions.|
|[`twig process layout`](./process-layout.md)|Show the server-defined form layout (tabs, boxes, ordered fields) for a work item type.|
|[`twig process description`](./process-description.md)|Write a byte-stable structural description of the process, for diffing.|
|[`twig states`](./states.md)|Hidden alias: list states for the active work item's type.|

## Notes

- `twig process` and `twig process layout` accept `--org`/`--project`
  overrides that switch to a live read of another project's process without
  needing a workspace. `twig process description` operates on the current
  workspace's process only.
- `twig states` is a compatibility alias that resolves the active work item
  and forwards to `twig process <type>`. It requires a workspace, and does
  not accept the override flags.
- Type-and-state data comes from the local workspace cache (populated by
  `twig sync`), except on the override paths, which read live from ADO. The
  hidden-type filter (`--include-hidden`) is derived from
  `Microsoft.HiddenCategory` membership, never a name list.
