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
    /// <summary>
    /// The base-process reference the shipped profile declares. T1 §6.2 compares
    /// this byte-equal, so the tests supply the matching value on the green path
    /// and a different one to drive <c>base-process-parent-mismatch</c>.
    /// </summary>
    private const string ShippedParentRef = "b8a3a935-7e91-48b8-a94c-606d37c3e9f2";

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
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        var result = provider.ValidateAgainstLiveProcess(ShippedShapedLiveProvider(), ShippedParentRef);
        result.IsSuccess.ShouldBeTrue(result.Error);
    }

    /// <summary>
    /// T1 §6.2 — the check that was declared but unreachable until AB#735.
    /// </summary>
    /// <remarks>
    /// <c>base-process-parent-mismatch</c> existed as an identifier while
    /// nothing compared <c>baseProcess.parentRef</c> to anything, because T1
    /// §8.1 gave the method no live parent reference to compare against and T1
    /// §6.2's premise that the value was "already reachable via
    /// <c>AdoProcessConfigurationResponse</c>" is false — that DTO carries
    /// backlog categories only. Passing the reference as data closes the gap.
    /// </remarks>
    [Fact]
    public void Live_process_derived_from_a_different_base_is_rejected()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());

        var result = provider.ValidateAgainstLiveProcess(
            ShippedShapedLiveProvider(),
            liveBaseProcessRef: "00000000-0000-0000-0000-000000000000");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.BaseProcessParentMismatch);
    }

    /// <summary>
    /// The parent reference is opaque and compared byte-equal, so a casing
    /// difference is a different process, not the same one spelled differently.
    /// </summary>
    [Fact]
    public void Base_process_reference_comparison_is_byte_exact()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());

        var result = provider.ValidateAgainstLiveProcess(
            ShippedShapedLiveProvider(), ShippedParentRef.ToUpperInvariant());

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(ReferenceProfileErrors.BaseProcessParentMismatch);
    }

    /// <summary>
    /// The §7.3 backstop now discriminates on the parent reference. Before
    /// AB#735 both fingerprint passes read that component off the PROFILE, so
    /// identical bytes landed on both sides and the backstop was structurally
    /// blind to the one dimension §6.2 covers.
    /// </summary>
    [Fact]
    public void Live_fingerprint_varies_with_the_live_base_process_reference()
    {
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        var live = ShippedShapedLiveProvider();

        var matching = provider.ComputeLiveFingerprint(live, ShippedParentRef);
        var divergent = provider.ComputeLiveFingerprint(live, "some-other-process");

        divergent.ShouldNotBe(matching);
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

        var result = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()).ValidateAgainstLiveProcess(live, ShippedParentRef);

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
        var provider = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching());
        var result = provider.ValidateAgainstLiveProcess(live, ShippedParentRef);
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
        var result = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()).ValidateAgainstLiveProcess(live, ShippedParentRef);
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
        var result = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()).ValidateAgainstLiveProcess(live, ShippedParentRef);
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
        var result = new EmbeddedReferenceProfileProvider(ProfilePinSources.Matching()).ValidateAgainstLiveProcess(live, ShippedParentRef);
        result.Error.ShouldBe(ReferenceProfileErrors.StateCategoryMismatch);
    }
}
