# PolyphonyRequiem.Twig.RenderTree

Pure presentation vocabulary for [twig](https://github.com/PolyphonyRequiem/twig) — a
high-performance .NET CLI for Azure DevOps work-item triage.

This package contains the render-tree node types that twig commands produce and that
format-specific renderers (Spectre human output, JSON, minimal) consume. It is
deliberately framework-agnostic:

- no `Spectre.Console` dependency
- no `ConsoleAppFramework` dependency
- no I/O
- AOT-clean — values are carried by a closed `RenderValue` discriminated union, not `object?`

The render tree is the seam that lets twig collapse the historical
`IOutputFormatter` family (one method per command × format) into a small number of
renderers (one `IRenderer.Write` method per node kind), tracked in ADO under
issue AB#3301.
