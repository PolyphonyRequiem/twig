using NSubstitute;
using Twig.Domain.Interfaces;

namespace Twig.Infrastructure.Tests;

/// <summary>
/// Builds <see cref="IIterationService"/> stubs that return a bound canonical identity
/// (ADO #1106, Spec #1103).
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

    public static IIterationService NewBound(
        string displayName = DefaultDisplayName,
        string uniqueName = DefaultCanonicalPrincipal)
        => Substitute.For<IIterationService>().WithBoundIdentity(displayName, uniqueName);
}
