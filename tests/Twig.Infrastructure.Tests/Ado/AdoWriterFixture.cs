using NSubstitute;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Puts the production ADO adapter (<see cref="AdoProviderWorkItemWriter"/>) in front of an
/// <see cref="IAdoWorkItemService"/> substitute, so tests exercise the real opaque-revision
/// precondition check on the provider mutation boundary instead of a hand-rolled port (#1178 AC (a)).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IAdoWorkItemService"/> no longer derives from <see cref="IProviderWorkItemWriter"/>,
/// so a test that used to pass the ADO substitute straight into a writer-typed constructor wraps it here.
/// </para>
/// <para>
/// <see cref="Wrap"/> also gives <c>PatchAsync</c> a default answer of <c>expectedRevision + 1</c>,
/// which is how ADO behaves. Without it an unstubbed patch answers 0, and the adapter refuses
/// a revision of 0 coming back from the wire (it is not a canonical revision). Call <see cref="Wrap"/>
/// <b>before</b> any per-test <c>PatchAsync</c> stub: NSubstitute uses the most recent matching
/// configuration, so a later specific stub wins, while one set before <see cref="Wrap"/> would be
/// overridden.
/// </para>
/// </remarks>
internal static class AdoWriterFixture
{
    public static IProviderWorkItemWriter Wrap(IAdoWorkItemService ado)
    {
        ado.PatchAsync(default, default!, default, default)
            .ReturnsForAnyArgs(ci => Task.FromResult(ci.ArgAt<int>(2) + 1));
        return new AdoProviderWorkItemWriter(ado);
    }

    /// <summary>The single ADO revision codec.</summary>
    public static IProviderRevisionCodec Codec => AdoProviderRevisionCodec.Instance;

    /// <summary>
    /// Puts the production ADO relation/delete adapter (<see cref="AdoProviderRevisionBoundWriter"/>)
    /// in front of a revision-bound client substitute, so link, unlink and delete tests run the real
    /// relation mapping and opaque-precondition decode (#1178 AC (b)).
    /// </summary>
    public static IProviderRevisionBoundWriter WrapRevisionBound(IRevisionBoundAdoWorkItemService ado)
        => new AdoProviderRevisionBoundWriter(ado);

    /// <summary>The production ADO seed-publish port over an orchestrator (#1178 AC (b)).</summary>
    public static IProviderSeedPublisher WrapSeedPublisher(Twig.Domain.Services.Seed.SeedPublishOrchestrator orchestrator)
        => new AdoProviderSeedPublisher(() => orchestrator);
}