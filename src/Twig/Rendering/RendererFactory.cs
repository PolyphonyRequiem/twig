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
    private readonly SpectreTheme? _theme;
    /// <summary>Creates a default renderer factory.</summary>
    public RendererFactory() { }
    internal RendererFactory(SpectreTheme theme) => _theme = theme;
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
    {
        return Twig.Formatters.OutputFormats.Normalize(format) switch
        {
            "json"         => new JsonRenderer(Console.Out, indented: true),
            "json-full"    => new JsonRenderer(Console.Out, indented: true),
            "json-compact" => new JsonRenderer(Console.Out, indented: true),
            "minimal"      => new MinimalRenderer(Console.Out),
            "ids"          => new IdsRenderer(Console.Out),
            _              => new SpectreNodeRenderer(CreateAnsiConsole(Console.Out), _theme),
        };
    }

    /// <summary>
    /// Returns an <see cref="IRenderer"/> bound to the supplied
    /// <paramref name="writer"/>. Use this when a command needs to route
    /// rendered output to a destination other than <c>Console.Out</c> —
    /// typically <c>Console.Error</c> for diagnostic output such as the
    /// static disambiguation fallback when interactive selection is not
    /// available.
    /// </summary>
    public IRenderer GetRenderer(string? format, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return Twig.Formatters.OutputFormats.Normalize(format) switch
        {
            "json"         => new JsonRenderer(writer, indented: true),
            "json-full"    => new JsonRenderer(writer, indented: true),
            "json-compact" => new JsonRenderer(writer, indented: true),
            "minimal"      => new MinimalRenderer(writer),
            "ids"          => new IdsRenderer(writer),
            _              => new SpectreNodeRenderer(CreateAnsiConsole(writer), _theme),
        };
    }

    internal IRenderer GetHumanRenderer(TextWriter writer, int? width, string color)
        => new SpectreNodeRenderer(CreateExplicitConsole(writer, width, color == "always"), _theme);

    internal (string Text, bool Ansi) CaptureHuman(RenderTree.RenderTree tree, int width, string color)
    {
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var console = CreateExplicitConsole(writer, width, color == "always");
        new SpectreNodeRenderer(console, _theme).Render(tree);
        return (writer.ToString(), console.Profile.Capabilities.Ansi);
    }

    private static IAnsiConsole CreateExplicitConsole(TextWriter writer, int? width, bool ansi)
    {

        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansi ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        });
        // Explicit opt-in works over a pipe without inferring terminal capabilities.
        console.Profile.Capabilities.Ansi = ansi;
        console.Profile.Capabilities.Links = ansi;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.Unicode = true;
        console.Profile.Capabilities.ColorSystem = ansi ? ColorSystem.TrueColor : ColorSystem.NoColors;
        console.Profile.Width = width ?? int.MaxValue;
        return console;
    }

    private static IAnsiConsole CreateAnsiConsole(TextWriter writer)
    {
        // Only a real, live stdout can emit colour. Capture writers and pipes keep the
        // existing deterministic plain behavior; TERM alone is never sufficient.
        if (ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected)
        {
            return AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.Detect, Interactive = InteractionSupport.No,
            });
        }
        // Render plain text unconditionally. Spectre's auto-detection of
        // ANSI support keys off the TERM env var when the upstream writer
        // is not a terminal — on Linux CI runners TERM=xterm-256color is
        // set, so Spectre emits ANSI escape codes even when stdout has
        // been redirected to a StringWriter (tests) or a pipe (CI logs,
        // `twig … | cat`). On Windows TERM is unset so Spectre stays
        // plain; the divergence breaks tests that assert on rendered
        // output.
        //
        // Disabling ANSI/colour unconditionally here gives deterministic
        // output across platforms. Box-drawing characters (tables, trees)
        // still render via Unicode, which works fine in non-TTY contexts.
        // HumanOutputFormatter keeps its own hardcoded ANSI codes for the
        // legacy paths that target live terminals directly, so interactive
        // colour output is preserved through that surface.
        var settings = new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
        };
        var console = AnsiConsole.Create(settings);
        // Belt-and-braces: Spectre's `AnsiSupport.No` setting is honoured by
        // its capability detector, but on some platforms (notably GitHub
        // Actions Linux runners) Spectre still emits ANSI escape codes for
        // styling markup like `[bold]…[/]` even after construction. Force
        // the profile capabilities explicitly so the renderer never writes
        // escape sequences.
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Capabilities.Links = false;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.Unicode = true;
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        // Disable hard wrapping for migrated commands. Legacy `HumanOutputFormatter`
        // wrote raw strings via `Console.WriteLine` which never wraps, and tests
        // (plus pipelines) rely on long success messages staying on one line.
        // Spectre defaults to 80-column width when stdout is redirected.
        console.Profile.Width = int.MaxValue;
        return console;
    }
}
