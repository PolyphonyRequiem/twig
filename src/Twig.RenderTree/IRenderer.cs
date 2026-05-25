namespace Twig.RenderTree;

/// <summary>
/// Renders a <see cref="RenderTree"/> to a pre-bound output target.
/// </summary>
/// <remarks>
/// <para>
/// Each implementation binds its output in its constructor (e.g. Spectre's
/// <c>IAnsiConsole</c>, a <c>TextWriter</c> for JSON/minimal renderers). The interface
/// is intentionally output-agnostic so commands can dispatch by user-selected
/// <c>--format</c> through one shape:
/// </para>
/// <code>
/// IRenderer renderer = format switch
/// {
///     "human"   =&gt; new SpectreRenderer(console),
///     "json"    =&gt; new JsonRenderer(writer),
///     "minimal" =&gt; new MinimalRenderer(writer),
///     _         =&gt; throw new ArgumentOutOfRangeException(),
/// };
/// renderer.Render(tree);
/// </code>
/// <para>
/// This interface is added in AB#3301 slice 2; concrete text renderers and command
/// migrations follow in subsequent slices.
/// </para>
/// </remarks>
public interface IRenderer
{
    /// <summary>Render the given tree to this renderer's pre-bound output target.</summary>
    void Render(RenderTree tree);
}
