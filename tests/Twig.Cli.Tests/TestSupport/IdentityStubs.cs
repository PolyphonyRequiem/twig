using NSubstitute;
using Twig.Domain.Interfaces;

namespace Twig.Cli.Tests.TestSupport;

/// <summary>
/// Builds <see cref="IIterationService"/> stubs that return a bound canonical identity
/// (ADO #1106, Spec #1103). The real connection route carries a <c>uniqueName</c>; tests
/// assume it is present unless they are specifically asserting the fail-closed refusal
/// of a connection that cannot supply one.
/// </summary>
internal static class IdentityStubs
{
    /// <summary>
    /// Canonical identity used by every test fixture that does not care which identity it is —
    /// only that one is bound.
    /// </summary>
    public const string DefaultCanonicalPrincipal = "self@tests.twig";

    /// <summary>Default display rendering paired with <see cref="DefaultCanonicalPrincipal"/>.</summary>
    public const string DefaultDisplayName = "Test Self";

    /// <summary>
    /// Stubs <see cref="IIterationService.GetAuthenticatedUserIdentityAsync"/> on
    /// <paramref name="service"/> so a canonical identity is returned.
    /// </summary>
    public static IIterationService WithBoundIdentity(
        this IIterationService service,
        string displayName = DefaultDisplayName,
        string uniqueName = DefaultCanonicalPrincipal)
    {
        service.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>((displayName, uniqueName)));
        return service;
    }

    /// <summary>
    /// Stubs the identity call to return no canonical name, modelling a connection that cannot
    /// prove its bound principal. Self consumers refuse rather than widen; <c>--all</c> consumers
    /// never reach this call.
    /// </summary>
    public static IIterationService WithoutBoundIdentity(
        this IIterationService service,
        string? displayName = DefaultDisplayName)
    {
        service.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(string? DisplayName, string? UniqueName)>((displayName, null)));
        return service;
    }

    /// <summary>Creates a stand-alone substitute wired with a bound identity.</summary>
    public static IIterationService NewBound(
        string displayName = DefaultDisplayName,
        string uniqueName = DefaultCanonicalPrincipal)
        => Substitute.For<IIterationService>().WithBoundIdentity(displayName, uniqueName);
}
