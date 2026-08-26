namespace Twig.Rendering;

/// <summary>
/// Explicit, opt-in presentation controls for the human renderer (AB#776).
/// </summary>
/// <remarks>
/// <para>
/// Colour and width are <strong>never</strong> auto-detected — not from <c>TERM</c>,
/// <c>COLORTERM</c>, <c>NO_COLOR</c>, nor from TTY state. Auto-detection cannot serve
/// the driving consumer: an external host capturing twig over a pipe has no TTY, so an
/// <c>auto</c> mode would resolve to "no colour" and push the caller into spoofing
/// environment variables around the child process. An explicit flag is a contract;
/// environment sniffing is a guess.
/// </para>
/// <para>
/// <see cref="Default"/> reproduces the historical behaviour exactly — no colour, no
/// wrapping — so every caller and test that does not opt in sees byte-identical output.
/// </para>
/// </remarks>
public sealed record HumanRenderOptions
{
    /// <summary>
    /// Today's behaviour: ANSI and colour disabled, width unbounded. Callers that do
    /// not opt in must be byte-identical to the pre-AB#776 renderer.
    /// </summary>
    public static readonly HumanRenderOptions Default = new();

    /// <summary>
    /// When <see langword="true"/>, the human renderer emits real ANSI colour
    /// (<c>--color always</c>). When <see langword="false"/>, it emits none
    /// (<c>--color never</c>). There is deliberately no third, detected value.
    /// </summary>
    public bool Color { get; init; }

    /// <summary>
    /// Explicit console width in columns (<c>--width &lt;n&gt;</c>), or
    /// <see langword="null"/> to leave output unwrapped. Spectre would otherwise
    /// assume 80 columns whenever stdout is redirected, which silently rewraps piped
    /// output; unbounded is the safe default for a machine-captured surface.
    /// </summary>
    public int? Width { get; init; }
}
