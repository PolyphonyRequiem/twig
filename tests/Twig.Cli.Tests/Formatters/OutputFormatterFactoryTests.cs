using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// After AB#3301 retired the machine-shape formatters, the factory always
/// returns <see cref="HumanOutputFormatter"/>. These tests pin that contract
/// so the few callers that still inject an <see cref="IOutputFormatter"/>
/// keep getting a working instance for every documented format alias.
/// </summary>
public class OutputFormatterFactoryTests
{
    private readonly OutputFormatterFactory _factory = new(new HumanOutputFormatter());

    [Theory]
    [InlineData("human")]
    [InlineData("HUMAN")]
    [InlineData("Human")]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("ids")]
    [InlineData("xyz")]
    [InlineData("")]
    public void GetFormatter_AnyFormat_ReturnsHumanOutputFormatter(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<HumanOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_Null_ReturnsHumanOutputFormatter()
    {
        _factory.GetFormatter(null!).ShouldBeOfType<HumanOutputFormatter>();
    }
}
