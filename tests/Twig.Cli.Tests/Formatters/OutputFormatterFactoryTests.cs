using Shouldly;
using Twig.Formatters;
using Xunit;

namespace Twig.Cli.Tests.Formatters;

public class OutputFormatterFactoryTests
{
    private readonly OutputFormatterFactory _factory = new(
        new HumanOutputFormatter(),
        new JsonOutputFormatter(),
        new JsonCompactOutputFormatter(new JsonOutputFormatter()),
        new MinimalOutputFormatter());

    [Fact]
    public void GetFormatter_Json_ReturnsJsonOutputFormatter()
    {
        _factory.GetFormatter("json").ShouldBeOfType<JsonOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_JsonFull_ReturnsJsonOutputFormatter()
    {
        _factory.GetFormatter("json-full").ShouldBeOfType<JsonOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_JsonCompact_ReturnsJsonCompactOutputFormatter()
    {
        _factory.GetFormatter("json-compact").ShouldBeOfType<JsonCompactOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_Minimal_ReturnsMinimalOutputFormatter()
    {
        _factory.GetFormatter("minimal").ShouldBeOfType<MinimalOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_Human_ReturnsHumanOutputFormatter()
    {
        _factory.GetFormatter("human").ShouldBeOfType<HumanOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_UnknownString_FallsBackToHumanOutputFormatter()
    {
        _factory.GetFormatter("xyz").ShouldBeOfType<HumanOutputFormatter>();
    }

    [Theory]
    [InlineData("JSON")]
    [InlineData("Json")]
    [InlineData("jSoN")]
    public void GetFormatter_Json_CaseInsensitive(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<JsonOutputFormatter>();
    }

    [Theory]
    [InlineData("JSON-FULL")]
    [InlineData("Json-Full")]
    public void GetFormatter_JsonFull_CaseInsensitive(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<JsonOutputFormatter>();
    }

    [Theory]
    [InlineData("JSON-COMPACT")]
    [InlineData("Json-Compact")]
    public void GetFormatter_JsonCompact_CaseInsensitive(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<JsonCompactOutputFormatter>();
    }

    [Theory]
    [InlineData("MINIMAL")]
    [InlineData("Minimal")]
    [InlineData("mInImAl")]
    public void GetFormatter_Minimal_CaseInsensitive(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<MinimalOutputFormatter>();
    }

    [Theory]
    [InlineData("HUMAN")]
    [InlineData("Human")]
    public void GetFormatter_Human_CaseInsensitive(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<HumanOutputFormatter>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("plaintext")]
    public void GetFormatter_InvalidFormat_FallsBackToHuman(string format)
    {
        _factory.GetFormatter(format).ShouldBeOfType<HumanOutputFormatter>();
    }

    [Fact]
    public void GetFormatter_Null_FallsBackToHumanOutputFormatter()
    {
        _factory.GetFormatter(null!).ShouldBeOfType<HumanOutputFormatter>();
    }
}
