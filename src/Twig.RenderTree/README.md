# PolyphonyRequiem.Twig.RenderTree

Pure presentation vocabulary for [twig](https://github.com/PolyphonyRequiem/twig) — a
high-performance .NET CLI for Azure DevOps work-item triage.

This package contains the render-tree node types that twig commands produce and the
format-specific renderers that consume them. It is deliberately framework-agnostic:

- no `Spectre.Console` dependency
- no `ConsoleAppFramework` dependency
- no I/O beyond the `TextWriter` target passed to text renderers
- AOT-clean — values are carried by a closed `RenderValue` discriminated union, not `object?`

The package ships:

- The `RenderTree` vocabulary: `RenderNode`, `RenderRow`, `RenderCell`, `RenderColumn`,
  `RenderValue`, `RenderTreeBranch`, `Severity`.
- The `IRenderer` interface.
- Text renderers — `MinimalRenderer` (tab-separated, line-oriented for piping) and
  `IdsRenderer` (bare numeric IDs, one per line).

The Spectre.Console human-format renderer lives in twig's CLI assembly to keep this
package free of the Spectre dependency.

The render tree is the seam that lets twig collapse the historical
`IOutputFormatter` family (one method per command × format) into a small number of
renderers (one `IRenderer.Write` method per node kind), tracked in ADO under
issue AB#3301.
