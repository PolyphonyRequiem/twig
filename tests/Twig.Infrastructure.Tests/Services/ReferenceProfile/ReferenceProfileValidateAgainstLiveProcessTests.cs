using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Services.ReferenceProfile;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// Command-time compatibility (T1 §7.2). Every failure route named in the T1
/// table gets a test; the shape of "correct compatibility" is asserted by the
/// green case at the top.
/// </summary>
public sealed class ReferenceProfileValidateAgainstLiveProcessTests
{
    private static readonly StateEntry[] BasicStates =
    [
        new("To Do", StateCategory.Proposed, Color: null),
        new("Doing", StateCategory.InProgress, Color: null),
        new("Done", StateCategory.Completed, Color: null),
    ];

    private static ProcessConfiguration BuildLive(params (string TypeName, StateEntry[] States)[] typeRows)
    {
        var records = typeRows
            .Select(r => new ProcessTypeRecord
            {
                TypeName = r.TypeName,
                States = r.States,
                ValidChildTypes = Array.Empty<string>(),
            })
            .ToArray();
        return ProcessConfiguration.FromRecords(records);
    }

    private static IProcessConfigurationProvider LiveProviderFor(ProcessConfiguration config)
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);
        return provider;
    }

    private static IProcessConfigurationProvider ShippedShapedLiveProvider()
    {
        // Mirrors the shipped profile.json bindings exactly.
        return LiveProviderFor(BuildLive(
            ("Initiative", BasicStates),
            ("Investigation", BasicStates),
            ("Feature", BasicStates),
            ("Bug", BasicStates),
            ("Task", BasicStates)));
    }

    [Fact]
    public void Shipped_profile_validates_against_matching_live_process()
    {
        var provider = new EmbeddedReferenceProfileProvider();
        var result = provider.ValidateAgainstLiveProcess(ShippedShapedLiveProvider());
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    /// <summary>
    /// The casing ADO actually mints. Verified 2026-08-27 against the
    /// Twig-Reference-Sandbox project (process a0afde20-50eb-4e30-b442-c9e7f13e752a):
    /// custom types are created with "To do" while the inherited Task keeps
    /// Basic's "To Do" — both observable on one board. Twig learned this the
    /// hard way on AB#79/AB#369 for transitions; this pins the same tolerance
    /// on the profile path so a future switch to an ordinal comparer cannot
    /// silently reject every real sandbox built by the AB#733 §2 recipe.
    /// </summary>
    [Fact]
    public void Shipped_profile_validates_when_live_custom_type_state_casing_differs()
    {
        StateEntry[] adoMintedCustomTypeStates =
        [
            new("To do", StateCategory.Proposed, Color: null),
            new("Doing", StateCategory.InProgress, Color: null),
            new("Done", StateCategory.Completed, Color: null),
        ];

        var live = LiveProviderFor(BuildLive(
            ("Initiative", adoMintedCustomTypeStates),
            ("Investigation", adoMintedCustomTypeStates),
            ("Feature", adoMintedCustomTypeStates),
            ("Bug", adoMintedCustomTypeStates),
            ("Task", BasicStates)));

        var result = new EmbeddedReferenceProfileProvider().ValidateAgainstLiveProcess(live);

        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    [Fact]
    public void Type_name_missing_when_role_binding_absent()
    {
        // Bind role Bug's typeName (which is "Bug") missing from live.
        var live = LiveProviderFor(BuildLive(
            ("Initiative", BasicStates),
            ("Investigation", BasicStates),
            ("Feature", BasicStates),
            // Bug missing.
            ("Task", BasicStates)));
        var provider = new EmbeddedReferenceProfileProvider();
        var result = provider.ValidateAgainstLiveProcess(live);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.TypeNameMissing);
    }

    [Fact]
    public void Live_has_extra_state_when_live_declares_state_the_profile_doesnt()
    {
        var extra = BasicStates.Concat([new StateEntry("Blocked", StateCategory.InProgress, null)]).ToArray();
        var live = LiveProviderFor(BuildLive(
            ("Initiative", BasicStates),
            ("Investigation", BasicStates),
            ("Feature", extra),
            ("Bug", BasicStates),
            ("Task", BasicStates)));
        var result = new EmbeddedReferenceProfileProvider().ValidateAgainstLiveProcess(live);
        result.Error.ShouldBe(ReferenceProfileErrors.LiveHasExtraState);
    }

    [Fact]
    public void State_order_mismatch_when_live_order_shifted()
    {
        var reordered = new[] { BasicStates[1], BasicStates[0], BasicStates[2] };
        var live = LiveProviderFor(BuildLive(
            ("Initiative", BasicStates),
            ("Investigation", BasicStates),
            ("Feature", reordered),
            ("Bug", BasicStates),
            ("Task", BasicStates)));
        var result = new EmbeddedReferenceProfileProvider().ValidateAgainstLiveProcess(live);
        result.Error.ShouldBe(ReferenceProfileErrors.StateOrderMismatch);
    }

    [Fact]
    public void State_category_mismatch_when_a_shared_state_reassigns_category()
    {
        var recategorized = new[]
        {
            BasicStates[0],
            new StateEntry("Doing", StateCategory.Resolved, null), // was InProgress
            BasicStates[2],
        };
        var live = LiveProviderFor(BuildLive(
            ("Initiative", BasicStates),
            ("Investigation", BasicStates),
            ("Feature", recategorized),
            ("Bug", BasicStates),
            ("Task", BasicStates)));
        var result = new EmbeddedReferenceProfileProvider().ValidateAgainstLiveProcess(live);
        result.Error.ShouldBe(ReferenceProfileErrors.StateCategoryMismatch);
    }
}
