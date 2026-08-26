using Spectre.Console;
using Twig.RenderTree;

namespace Twig.Rendering;

/// <summary>
/// Resolves an <see cref="IRenderer"/> by output-format name. Mirrors the
/// alias normalization of
/// <see cref="Twig.Formatters.OutputFormatterFactory"/>: <c>json</c>,
/// <c>json-full</c>, and <c>json-compact</c> all resolve to
/// <see cref="JsonRenderer"/>; <c>minimal</c> resolves to
/// <see cref="MinimalRenderer"/>; <c>ids</c> resolves to
/// <see cref="IdsRenderer"/>; anything else (or <c>human</c>) resolves to
/// <see cref="SpectreNodeRenderer"/>.
/// </summary>
/// <remarks>
/// <para>
/// AOT-safe: uses a compile-time switch expression, no reflection.
/// </para>
/// <para>
/// Renderers are constructed per call rather than cached so the output sink
/// (<c>Console.Out</c> and the Spectre <see cref="IAnsiConsole"/>) is bound
/// at render time. This matters for tests that swap <c>Console.Out</c> via
/// <c>StdoutCapture</c>: a renderer constructed earlier would bind the
/// pre-swap writer and the test would see no output.
/// </para>
/// </remarks>
public sealed class RendererFactory
{
    /// <summary>The default format used when none is specified.</summary>
    public const string DefaultFormat = Twig.Formatters.OutputFormats.Default;

    /// <summary>
    /// Returns an <see cref="IRenderer"/> bound to the current
    /// <c>Console.Out</c>. <see cref="JsonRenderer"/> currently emits
    /// indented (pretty) JSON for all JSON aliases — commands needing a
    /// slimmer compact-schema variant project differently per format at the
    /// tree level.
    /// </summary>
    /// <remarks>
    /// Wayfinder 0019: format membership is decided by
    /// <see cref="Twig.Formatters.OutputFormats"/>, the single accept-list.
    /// Unknown values are rejected at the entrypoint and cannot reach here
    /// from the CLI.
    /// </remarks>
    public IRenderer GetRenderer(string? format)
        => this.GetRenderer(format, Console.Out, HumanRenderOptions.Default);

    /// <summary>
    /// Returns an <see cref="IRenderer"/> bound to the current <c>Console.Out</c>,
    /// with explicit human-render options (AB#776). Non-human formats ignore
    /// <paramref name="options"/> entirely — colour and width are presentation-only.
    /// </summary>
    public IRenderer GetRenderer(string? format, HumanRenderOptions options)
        => this.GetRenderer(format, Console.Out, options);

    /// <summary>
    /// Returns an <see cref="IRenderer"/> bound to the supplied
    /// <paramref name="writer"/>. Use this when a command needs to route
    /// rendered output to a destination other than <c>Console.Out</c> —
    /// typically <c>Console.Error</c> for diagnostic output such as the
    /// static disambiguation fallback when interactive selection is not
    /// available.
    /// </summary>
    public IRenderer GetRenderer(string? format, TextWriter writer)
        => this.GetRenderer(format, writer, HumanRenderOptions.Default);

    /// <summary>
    /// Returns an <see cref="IRenderer"/> bound to the supplied
    /// <paramref name="writer"/> with explicit human-render options (AB#776).
    /// </summary>
    /// <remarks>
    /// This is the single chokepoint for human colour and width: every command that
    /// has migrated onto the <see cref="IRenderer"/> seam inherits the behaviour from
    /// here, so opting a command in is a matter of passing
    /// <see cref="HumanRenderOptions"/> rather than touching the renderer.
    /// </remarks>
    public IRenderer GetRenderer(string? format, TextWriter writer, HumanRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);

        return Twig.Formatters.OutputFormats.Normalize(format) switch
        {
            "json"         => new JsonRenderer(writer, indented: true),
            "json-full"    => new JsonRenderer(writer, indented: true),
            "json-compact" => new JsonRenderer(writer, indented: true),
            "minimal"      => new MinimalRenderer(writer),
            "ids"          => new IdsRenderer(writer),
            _              => new SpectreNodeRenderer(CreateAnsiConsole(writer, options)),
        };
    }

    private static IAnsiConsole CreateAnsiConsole(TextWriter writer, HumanRenderOptions options)
    {
        // Colour is opt-in and explicit (AB#776) — never auto-detected.
        //
        // Spectre's own auto-detection keys off the TERM env var when the upstream
        // writer is not a terminal. On Linux CI runners TERM=xterm-256color is set, so
        // Spectre emits ANSI escape codes even when stdout has been redirected to a
        // StringWriter (tests) or a pipe (CI logs, `twig … | cat`); on Windows TERM is
        // unset so Spectre stays plain. That divergence is precisely why detection is
        // not a usable contract here, and why the caller states what it wants instead.
        //
        // With colour off, the settings below reproduce the historical unconditional
        // plain-text behaviour byte for byte. Box-drawing characters (tables, trees)
        // still render via Unicode either way, which works fine in non-TTY contexts.
        var settings = new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = options.Color ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = options.Color ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        };
        var console = AnsiConsole.Create(settings);

        // Belt-and-braces: Spectre's capability detector does not always honour the
        // settings above once the writer is redirected — on some platforms (notably
        // GitHub Actions Linux runners) it still emits escape codes for styling markup
        // like `[bold]…[/]`. Force the profile explicitly so the answer is the caller's
        // in both directions: never escape sequences when off, always when on.
        console.Profile.Capabilities.Ansi = options.Color;
        console.Profile.Capabilities.Links = false;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.Unicode = true;
        console.Profile.Capabilities.ColorSystem = options.Color
            ? ColorSystem.TrueColor
            : ColorSystem.NoColors;

        // Width is opt-in too. Unbounded is the default because legacy
        // `HumanOutputFormatter` wrote raw strings via `Console.WriteLine`, which never
        // wraps, and tests (plus pipelines) rely on long success messages staying on
        // one line. Spectre would otherwise assume 80 columns whenever stdout is
        // redirected and silently rewrap piped output.
        console.Profile.Width = options.Width ?? int.MaxValue;
        return console;
    }
}
