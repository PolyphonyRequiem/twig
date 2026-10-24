using Shouldly;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public sealed class ExternalWorkItemIdentityTests
{
    [Fact]
    public void Constructor_PreservesAdapterNormalizedComponents()
    {
        var identity = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_kwDOABc");

        identity.ProviderKind.ShouldBe("github");
        identity.ResourceScope.ShouldBe("https://api.github.com");
        identity.NativeId.ShouldBe("I_kwDOABc");
    }

    [Theory]
    [InlineData("", "scope", "id", "providerKind")]
    [InlineData("ado", " ", "id", "resourceScope")]
    [InlineData("ado", "org", "\t", "nativeId")]
    public void Constructor_RejectsMissingIdentityComponent(
        string providerKind,
        string resourceScope,
        string nativeId,
        string parameterName)
    {
        var exception = Should.Throw<ArgumentException>(
            () => new ExternalWorkItemIdentity(providerKind, resourceScope, nativeId));

        exception.ParamName.ShouldBe(parameterName);
    }

    [Fact]
    public void Constructor_RejectsUnnormalizedWhitespace()
    {
        Should.Throw<ArgumentException>(
            () => new ExternalWorkItemIdentity("ado", "https://dev.azure.com/org ", "42"))
            .ParamName.ShouldBe("resourceScope");
    }

    [Fact]
    public void Equality_IsOrdinalAndCasePreserving()
    {
        var lower = new ExternalWorkItemIdentity("github", "https://api.github.com", "I_kwDOABc");
        var differentCase = new ExternalWorkItemIdentity("github", "https://api.github.com", "i_kwdoabc");

        lower.ShouldNotBe(differentCase);
    }
}
