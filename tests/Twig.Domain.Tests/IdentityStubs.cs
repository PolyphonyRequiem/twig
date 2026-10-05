using NSubstitute;
using Twig.Domain.Interfaces;

namespace Twig.Domain.Tests;

/// <summary>
/// Builds <see cref="IIterationService"/> stubs that return a bound canonical identity
/// (ADO #1106, Spec #1103). The real connection route carries a <c>uniqueName</c>; tests
/// assume one is present unless they are specifically asserting the fail-closed refusal.
/// </summary>
internal static class IdentityStubs
{
    public const string DefaultCanonicalPrincipal = "self@tests.twig";
    public const string DefaultDisplayName = "Test Self";

    public static IIterationService WithBoundIdentity(
        this IIterationService service,
        string displayName = DefaultDisplayName,
        string uniqueName = DefaultCanonicalPrincipal)
    {
        service.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>((displayName, uniqueName)));
        return service;
    }

    public static IIterationService WithoutBoundIdentity(
        this IIterationService service,
        string? displayName = DefaultDisplayName)
    {
        service.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>((displayName, null)));
        return service;
    }

    public static IIterationService NewBound(
        string displayName = DefaultDisplayName,
        string uniqueName = DefaultCanonicalPrincipal)
        => Substitute.For<IIterationService>().WithBoundIdentity(displayName, uniqueName);
}
