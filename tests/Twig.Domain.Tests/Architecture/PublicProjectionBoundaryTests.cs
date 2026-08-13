using System.Reflection;
using Shouldly;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Xunit;

namespace Twig.Domain.Tests.Architecture;

/// <summary>
/// Pins the accessibility coupling between the public detail projection and the layout
/// types it consumes (AB#253).
/// </summary>
/// <remarks>
/// <para>
/// <b>The conflict this exists to stop recurring.</b> Two closed rulings disagreed about
/// <see cref="FormLayout"/>'s visibility, and nothing in the tree recorded the dependency
/// between them:
/// </para>
/// <list type="bullet">
/// <item>
/// Implementation Decision 9 of <c>docs/specs/process-description.spec.md</c> said
/// <c>ProcessRule</c> and <c>FormLayout</c> both stay <c>internal</c>, because the
/// descriptor document declares itself <c>0.1</c> and a public type would assert a
/// stability the document explicitly warns against.
/// </item>
/// <item>
/// wayfinder-detail-projection tickets 0001/0003 (both <c>closed</c>, shipped as AB#155)
/// then promoted all five layout records <c>internal</c> → <c>public</c>, because
/// <see cref="WorkItemDetailProjector.Project"/> takes a <see cref="FormLayout"/> BY VALUE
/// and <c>samples/Twig.DetailHost</c> exists to prove an external consumer can call it
/// without referencing Twig.Infrastructure.
/// </item>
/// </list>
/// <para>
/// 🔴 <b>AB#253 ruled the later decision wins.</b> This was supersession, not drift: the
/// promotion was deliberate, reviewed, and load-bearing, and Decision 9 was written before
/// a real external consumer existed. Decision 9's clause is narrowed to <c>ProcessRule</c>,
/// which remains <c>internal</c> — see <see cref="ProcessRuleStaysInternalPerDecision9"/>.
/// </para>
/// <para>
/// <b>Why a test rather than a comment.</b> The two rulings live in different documents on
/// different branches, and the code carried no signal that demoting one type would take the
/// whole public projection contract with it. Attempting the demotion does not produce a
/// clear "you broke the boundary" message — it produces three
/// <c>CS0050</c>/<c>CS0051</c> inconsistent-accessibility errors inside Twig.Domain, which
/// read like a local mistake rather than a boundary decision being reversed. This test
/// names the consequence at the point someone would trip over it.
/// </para>
/// <para>
/// <b>If you are here because this test failed:</b> you are not fixing a visibility
/// modifier, you are reversing AB#155's external-host boundary. Take it to a ruling before
/// changing the assertions. Making these types <c>internal</c> also forces
/// <see cref="WorkItemDetailProjector"/>, <see cref="FallbackFormLayout"/> and the entire
/// <see cref="WorkItemDetailDocument"/> family <c>internal</c>, and deletes the reason
/// <c>samples/Twig.DetailHost</c> exists.
/// </para>
/// </remarks>
public sealed class PublicProjectionBoundaryTests
{
    /// <summary>
    /// The layout records an external host receives. Named individually rather than swept
    /// by namespace: a sweep would silently start covering a new type nobody decided to
    /// publish, which is the failure mode this whole file is about.
    /// </summary>
    public static TheoryData<Type> LayoutTypesOnThePublicBoundary() =>
    [
        typeof(FormLayout),
        typeof(LayoutPage),
        typeof(LayoutSection),
        typeof(LayoutGroup),
        typeof(LayoutControl),
    ];

    [Theory]
    [MemberData(nameof(LayoutTypesOnThePublicBoundary))]
    public void LayoutTypesArePublic_BecauseTheProjectionContractHandsThemToExternalHosts(
        Type layoutType)
    {
        layoutType.IsPublic.ShouldBeTrue(
            $"{layoutType.Name} is reachable from WorkItemDetailProjector.Project, which "
            + "samples/Twig.DetailHost calls as an external consumer. Demoting it to "
            + "internal reverses AB#155's boundary — see this class's remarks.");
    }

    /// <summary>
    /// The coupling itself, asserted directly rather than inferred from the two facts
    /// above. <see cref="LayoutTypesArePublic_BecauseTheProjectionContractHandsThemToExternalHosts"/>
    /// would still pass if someone changed <c>Project</c> to stop taking a
    /// <see cref="FormLayout"/>, at which point the promotion would no longer be justified
    /// and this file's reasoning would be stale without failing.
    /// </summary>
    [Fact]
    public void TheProjectionEntryPoint_TakesAFormLayoutByValue()
    {
        var project = typeof(WorkItemDetailProjector)
            .GetMethod(nameof(WorkItemDetailProjector.Project), BindingFlags.Public | BindingFlags.Static);

        project.ShouldNotBeNull(
            "WorkItemDetailProjector.Project is the public projection entry point that "
            + "justifies the layout types' visibility.");

        project.GetParameters()[0].ParameterType.ShouldBe(
            typeof(FormLayout),
            "Project takes the layout BY VALUE — that is precisely why FormLayout cannot be "
            + "internal. If this changed, re-examine whether the promotion is still earned.");
    }

    /// <summary>
    /// The other half of Implementation Decision 9, which AB#253 did NOT overturn.
    /// </summary>
    /// <remarks>
    /// Decision 9 named two types in one sentence and ranked them for later promotion:
    /// <i>"If only one is promoted later, promote the rule type first."</i> Reality went the
    /// other way — the layout type was promoted and the rule type was not — because the
    /// promotion was driven by a consumer that materialized rather than by the ranking.
    /// The ranking is therefore stale as a prediction, but the rule type's actual
    /// visibility still matches Decision 9 and is pinned here so a future promotion of it
    /// is a deliberate act.
    /// </remarks>
    [Theory]
    [InlineData("Twig.Domain.ValueObjects.ProcessRule")]
    [InlineData("Twig.Domain.ValueObjects.RuleCondition")]
    [InlineData("Twig.Domain.ValueObjects.RuleAction")]
    public void ProcessRuleStaysInternalPerDecision9(string typeName)
    {
        var ruleType = typeof(FormLayout).Assembly.GetType(typeName, throwOnError: true)!;

        ruleType.IsPublic.ShouldBeFalse(
            $"{typeName} stays internal per Implementation Decision 9. AB#253 narrowed that "
            + "decision to the rule types only; it did not promote them.");
    }
}
