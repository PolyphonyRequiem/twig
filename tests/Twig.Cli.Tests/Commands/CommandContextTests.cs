using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class CommandContextTests
{
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RenderingPipelineFactory _pipelineFactory;
    private readonly HintEngine _hintEngine;
    private readonly TwigConfiguration _config;

    public CommandContextTests()
    {
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        _config = new TwigConfiguration();

        var testConsole = new TestConsole();
        var renderer = new SpectreRenderer(testConsole, new SpectreTheme(new DisplayConfig()));
        _pipelineFactory = new RenderingPipelineFactory(_formatterFactory, renderer, isOutputRedirected: () => true);
    }

    [Fact]
    public void Resolve_Delegates_To_PipelineFactory()
    {
        var ctx = new CommandContext(_pipelineFactory, _formatterFactory, _hintEngine, _config);

        var (formatter, renderer) = ctx.Resolve("human");

        // Redirected output → sync path → renderer should be null
        formatter.ShouldNotBeNull();
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_With_NoLive_Delegates_To_PipelineFactory()
    {
        var testConsole = new TestConsole();
        var spectreRenderer = new SpectreRenderer(testConsole, new SpectreTheme(new DisplayConfig()));
        var ttyPipeline = new RenderingPipelineFactory(_formatterFactory, spectreRenderer, isOutputRedirected: () => false);
        var ctx = new CommandContext(ttyPipeline, _formatterFactory, _hintEngine, _config);

        var (formatter, renderer) = ctx.Resolve("human", noLive: true);

        formatter.ShouldNotBeNull();
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_Returns_Renderer_For_Tty_Human_Format()
    {
        var testConsole = new TestConsole();
        var spectreRenderer = new SpectreRenderer(testConsole, new SpectreTheme(new DisplayConfig()));
        var ttyPipeline = new RenderingPipelineFactory(_formatterFactory, spectreRenderer, isOutputRedirected: () => false);
        var ctx = new CommandContext(ttyPipeline, _formatterFactory, _hintEngine, _config);

        var (formatter, renderer) = ctx.Resolve("human");

        formatter.ShouldNotBeNull();
        renderer.ShouldNotBeNull();
    }

    [Fact]
    public void StderrWriter_Returns_Console_Error_When_Stderr_Is_Null()
    {
        var ctx = new CommandContext(_pipelineFactory, _formatterFactory, _hintEngine, _config);

        ctx.StderrWriter.ShouldBe(Console.Error);
    }

    [Fact]
    public void StderrWriter_Returns_Provided_TextWriter()
    {
        var writer = new StringWriter();
        var ctx = new CommandContext(_pipelineFactory, _formatterFactory, _hintEngine, _config, Stderr: writer);

        ctx.StderrWriter.ShouldBe(writer);
    }

    [Fact]
    public void Record_With_Expression_Creates_New_Instance()
    {
        var ctx = new CommandContext(_pipelineFactory, _formatterFactory, _hintEngine, _config);
        var telemetry = Substitute.For<ITelemetryClient>();

        var ctxWithTelemetry = ctx with { TelemetryClient = telemetry };

        ctxWithTelemetry.TelemetryClient.ShouldBe(telemetry);
        ctx.TelemetryClient.ShouldBeNull();
    }
}
