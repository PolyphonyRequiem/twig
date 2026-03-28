using NSubstitute;
using Shouldly;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public class RenderingPipelineFactoryTests
{
    private readonly IAsyncRenderer _asyncRenderer = Substitute.For<IAsyncRenderer>();

    private readonly OutputFormatterFactory _formatterFactory = new(
        new HumanOutputFormatter(),
        new JsonOutputFormatter(),
        new JsonCompactOutputFormatter(new JsonOutputFormatter()),
        new MinimalOutputFormatter());

    private RenderingPipelineFactory CreateFactory() =>
        new(_formatterFactory, _asyncRenderer);

    private RenderingPipelineFactory CreateFactory(Func<bool> isOutputRedirected) =>
        new(_formatterFactory, _asyncRenderer, isOutputRedirected);

    [Fact]
    public void Resolve_JsonFormat_ReturnsNullRenderer()
    {
        var factory = CreateFactory();
        var (formatter, renderer) = factory.Resolve("json");

        formatter.ShouldBeOfType<JsonOutputFormatter>();
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_MinimalFormat_ReturnsNullRenderer()
    {
        var factory = CreateFactory();
        var (formatter, renderer) = factory.Resolve("minimal");

        formatter.ShouldBeOfType<MinimalOutputFormatter>();
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_HumanFormat_WithNoLive_ReturnsNullRenderer()
    {
        var factory = CreateFactory();
        var (formatter, renderer) = factory.Resolve("human", noLive: true);

        formatter.ShouldBeOfType<HumanOutputFormatter>();
        renderer.ShouldBeNull();
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("Json")]
    public void Resolve_JsonFormat_CaseInsensitive_ReturnsNullRenderer(string format)
    {
        var factory = CreateFactory();
        var (_, renderer) = factory.Resolve(format);
        renderer.ShouldBeNull();
    }

    [Theory]
    [InlineData("MINIMAL")]
    [InlineData("Minimal")]
    public void Resolve_MinimalFormat_CaseInsensitive_ReturnsNullRenderer(string format)
    {
        var factory = CreateFactory();
        var (_, renderer) = factory.Resolve(format);
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_HumanFormat_WhenOutputRedirected_ReturnsNullRenderer()
    {
        // Console.IsOutputRedirected is true in test environments (output is redirected).
        // This test validates that the factory checks the flag — when redirected, renderer is null.
        var factory = CreateFactory();
        var (formatter, renderer) = factory.Resolve("human");

        formatter.ShouldBeOfType<HumanOutputFormatter>();

        // In test/CI environments, Console.IsOutputRedirected is typically true,
        // so the async renderer should be null (sync fallback).
        if (Console.IsOutputRedirected)
        {
            renderer.ShouldBeNull();
        }
        else
        {
            // If somehow running with a real TTY, renderer should be the async renderer
            renderer.ShouldBe(_asyncRenderer);
        }
    }

    [Fact]
    public void Resolve_UnknownFormat_FallsBackToHumanFormatter_NullRenderer()
    {
        // Unknown formats fall through to human formatter via OutputFormatterFactory,
        // but since format string doesn't match "human", renderer is null.
        var factory = CreateFactory();
        var (formatter, renderer) = factory.Resolve("unknown");

        formatter.ShouldBeOfType<HumanOutputFormatter>();
        renderer.ShouldBeNull();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    public void Resolve_NonHumanFormats_AlwaysReturnNullRenderer_RegardlessOfNoLive(string format)
    {
        var factory = CreateFactory();

        var (_, rendererWithLive) = factory.Resolve(format, noLive: false);
        rendererWithLive.ShouldBeNull();

        var (_, rendererNoLive) = factory.Resolve(format, noLive: true);
        rendererNoLive.ShouldBeNull();
    }

    [Fact]
    public void Resolve_ReturnsCorrectFormatterForEachFormat()
    {
        var factory = CreateFactory();

        factory.Resolve("human").Formatter.ShouldBeOfType<HumanOutputFormatter>();
        factory.Resolve("json").Formatter.ShouldBeOfType<JsonOutputFormatter>();
        factory.Resolve("minimal").Formatter.ShouldBeOfType<MinimalOutputFormatter>();
    }

    [Fact]
    public void Resolve_HumanFormat_TtyNotRedirected_ReturnsAsyncRenderer()
    {
        // Inject isOutputRedirected = false to simulate TTY environment
        var factory = CreateFactory(isOutputRedirected: () => false);
        var (formatter, renderer) = factory.Resolve("human");

        formatter.ShouldBeOfType<HumanOutputFormatter>();
        renderer.ShouldBe(_asyncRenderer);
    }

    [Fact]
    public void Resolve_HumanFormat_InjectedRedirected_ReturnsNullRenderer()
    {
        // Inject isOutputRedirected = true to simulate piped output
        var factory = CreateFactory(isOutputRedirected: () => true);
        var (formatter, renderer) = factory.Resolve("human");

        formatter.ShouldBeOfType<HumanOutputFormatter>();
        renderer.ShouldBeNull();
    }

    [Fact]
    public void Resolve_HumanFormat_TtyNotRedirected_WithNoLive_ReturnsNullRenderer()
    {
        var factory = CreateFactory(isOutputRedirected: () => false);
        var (formatter, renderer) = factory.Resolve("human", noLive: true);

        formatter.ShouldBeOfType<HumanOutputFormatter>();
        renderer.ShouldBeNull();
    }
}
