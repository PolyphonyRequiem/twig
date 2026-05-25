using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

/// <summary>
/// After AB#3301 retired the machine-shape formatters,
/// <see cref="HumanOutputFormatter"/> became the sole structured formatter.
/// To keep stderr messages plain-text for non-interactive consumers (CI
/// logs, jq pipelines) the factory wraps the human formatter in an
/// ANSI-stripping <see cref="PlainOutputFormatter"/> when a machine format
/// alias is requested. These tests pin both halves of the contract.
/// </summary>
public class OutputFormatterFactoryTests
{
    private readonly OutputFormatterFactory _factory = new(new HumanOutputFormatter());

    [Theory]
    [InlineData("human")]
    [InlineData("HUMAN")]
    [InlineData("Human")]
    [InlineData("xyz")]
    [InlineData("")]
    public void GetFormatter_HumanOrUnknownFormat_ReturnsHumanOutputFormatter(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<HumanOutputFormatter>();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public void GetFormatter_MachineFormat_ReturnsPlainAnsiStrippingFormatter(string format)
    {
        var fmt = _factory.GetFormatter(format);

        // Sanity-check the contract by exercising a styled message: the
        // returned formatter must emit no ANSI escape codes for machine
        // formats, regardless of how HumanOutputFormatter would style it.
        fmt.FormatInfo("hello").ShouldNotContain("\x1b[");
        fmt.FormatError("boom").ShouldNotContain("\x1b[");
        fmt.FormatSuccess("ok").ShouldNotContain("\x1b[");
        fmt.FormatHint("tip").ShouldNotContain("\x1b[");
    }

    [Fact]
    public void GetFormatter_Null_ReturnsHumanOutputFormatter()
    {
        _factory.GetFormatter(null!).ShouldBeOfType<HumanOutputFormatter>();
    }
}
