namespace Twig.RenderTree;

/// <summary>
/// Top-level document produced by a twig command and consumed by a format-specific
/// renderer. A <c>RenderTree</c> is an ordered list of <see cref="RenderNode"/>s;
/// each node is a typed presentation intent (text, table, tree view, …).
/// </summary>
/// <remarks>
/// <para>
/// This type is the seam that lets twig collapse the historical
/// <c>IOutputFormatter</c> family (one method per command × format) into a small
/// set of renderers (one <c>IRenderer.Write</c> method per node kind). See AB#3301.
/// </para>
/// <para>
/// The render tree carries no rendering-library types (no Spectre.Console markup,
/// no ANSI escapes). It carries semantic intent only — <see cref="Severity"/>,
/// hierarchy, structure — and lets the renderer choose how to express it.
/// </para>
/// </remarks>
public sealed record RenderTree(IReadOnlyList<RenderNode> Nodes);
