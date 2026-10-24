using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class ProviderRevisionTokenTests
{
    [Theory]
    [InlineData("42")]
    [InlineData("\"etag-value\"")]
    [InlineData("W/\"weak-tag\"")]
    public void Constructor_PreservesOpaqueToken(string value)
    {
        new ProviderRevisionToken(value).Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsUnavailableToken(string value)
    {
        Should.Throw<ArgumentException>(() => new ProviderRevisionToken(value));
    }
}
