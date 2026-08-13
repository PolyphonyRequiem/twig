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
/// Implementation Decision 9 of <c>docs/specs/process-description.spec.md</c> (branch
/// <c>docs/process-descriptor-map</c>) said <c>ProcessRule</c> and <see cref="FormLayout"/>
/// both stay <c>internal</c>, because the descriptor document declares itself <c>0.1</c>
/// and a public type would assert a stability the document explicitly warns against.
/// </item>
/// <item>
/// wayfinder-detail-projection ticket 0003 (<c>closed</c>, shipped as AB#155, commit
/// <c>25d9f59d</c>) then promoted all five layout records <c>internal</c> → <c>public</c>,
/// as ticket 0001 (<c>closed</c>) had scoped, because
/// <see cref="WorkItemDetailProjector.Project"/> exposes <see cref="FormLayout"/> in its
/// public signature and <c>samples/Twig.DetailHost</c> exists to prove an external consumer
/// can call it without referencing Twig.Infrastructure.
/// </item>
/// </list>
/// <para>
/// 🔴 <b>AB#253 ruled the later decision wins.</b> This was supersession, not drift: the
/// promotion was deliberate, reviewed, and load-bearing, and Decision 9 was written before
/// a real external consumer existed. Decision 9's clause is narrowed to <c>ProcessRule</c>,
/// which remains <c>internal</c> — see <see cref="RuleTypesStayInternalPerDecision9"/>.
/// </para>
/// <para>
/// <b>What this file adds over the analyzers, stated precisely — because overstating it is
/// how a guard earns undeserved trust.</b> A demotion is ALREADY caught twice without this
/// file, and both are earlier and louder:
/// </para>
/// <list type="number">
/// <item>
/// A <i>partial</i> demotion (the layout records alone) does not compile — three
/// <c>CS0050</c>/<c>CS0051</c> inconsistent-accessibility errors inside Twig.Domain, before
/// the sample host is even reached. No test runs, because the assembly under test never
/// builds.
/// </item>
/// <item>
/// A <i>completed</i> demotion (the projection contract taken down with it, so the code
/// compiles again) is caught by <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> as
/// <c>RS0017</c> — measured at 924 errors, one per manifest entry that would no longer be
/// public — because <c>TreatWarningsAsErrors</c> is on.
/// </item>
/// </list>
/// <para>
/// So the visibility assertions here are a NAMED failure in front of a cryptic one, not the
/// only line of defence. What is genuinely unguarded elsewhere, and the reason this file is
/// not ceremony, is
/// <see cref="TheProjectionEntryPoint_ExposesFormLayoutInItsPublicSignature"/>: no analyzer
/// notices if <c>Project</c> stops taking a <see cref="FormLayout"/>, at which point the
/// promotion loses its justification and every other assertion here would keep passing while
/// the reasoning behind them had quietly expired.
/// </para>
/// <para>
/// <b>If you are here because this test failed:</b> you are not fixing a visibility
/// modifier, you are reversing AB#155's external-host boundary. Take it to a ruling before
/// changing the assertions.
/// </para>
/// </remarks>
public sealed class PublicProjectionBoundaryTests
{
    /// <summary>
    /// The namespace-qualified prefix of the layout value objects, used by the completeness
    /// sweep below.
    /// </summary>
    private const string ValueObjectsNamespace = "Twig.Domain.ValueObjects";

    /// <summary>
    /// Every type an external host touches to receive a projected work item: the five layout
    /// records, the two entry points that hand them over, and the document family that comes
    /// back.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>The projection types are here deliberately, and their absence was a real hole.</b>
    /// Reflection reports a <c>public static</c> method on an <c>internal</c> type as public,
    /// so pinning only the layout records left a hole big enough to drive the whole boundary
    /// through: demoting <see cref="WorkItemDetailProjector"/> itself would have kept every
    /// assertion in this file green while deleting the thing the file exists to protect.
    /// </remarks>
    public static TheoryData<Type> TypesOnThePublicProjectionBoundary() =>
    [
        typeof(FormLayout),
        typeof(LayoutPage),
        typeof(LayoutSection),
        typeof(LayoutGroup),
        typeof(LayoutControl),
        typeof(WorkItemDetailProjector),
        typeof(FallbackFormLayout),
        typeof(WorkItemDetailDocument),
    ];

    [Theory]
    [MemberData(nameof(TypesOnThePublicProjectionBoundary))]
    public void TheProjectionBoundaryIsPublic(Type boundaryType)
    {
        // IsVisible, not IsPublic: IsPublic reports false for a PUBLIC NESTED type, which is
        // externally reachable. Nothing here is nested today, so the two agree — but a future
        // refactor that nests one of these would make IsPublic fail spuriously, and the
        // negative arm below would fail to fire at all. IsVisible accounts for the whole
        // nesting chain and is correct under both shapes.
        boundaryType.IsVisible.ShouldBeTrue(
            $"{boundaryType.Name} is part of the external-host projection boundary that "
            + "samples/Twig.DetailHost consumes. Demoting it reverses AB#155 — see this "
            + "class's remarks.");
    }

    /// <summary>
    /// The list above cannot silently stop covering the layout surface.
    /// </summary>
    /// <remarks>
    /// Both sibling guards in this directory carry a sweep like this, for the same reason: a
    /// hardcoded inventory with no completeness check quietly narrows as the codebase grows,
    /// and a guard that has stopped covering things still reports green. If a sixth public
    /// layout record appears, this fails and someone decides deliberately whether it belongs
    /// on the external boundary.
    /// </remarks>
    [Fact]
    public void TheLayoutSurfaceHasNotGrownUnnoticed()
    {
        var publicLayoutTypes = typeof(FormLayout).Assembly
            .GetTypes()
            .Where(type => type.Namespace == ValueObjectsNamespace)
            .Where(type => type.IsVisible)
            .Where(type => type.Name is "FormLayout" || type.Name.StartsWith("Layout", StringComparison.Ordinal))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        publicLayoutTypes.ShouldNotBeEmpty(
            "a sweep that matches nothing would pass vacuously and guard nothing.");

        publicLayoutTypes.ShouldBe(
            ["FormLayout", "LayoutControl", "LayoutGroup", "LayoutPage", "LayoutSection"],
            "the public layout surface changed. If a new layout type is genuinely part of "
            + "the external-host boundary, add it here and to "
            + $"{nameof(TypesOnThePublicProjectionBoundary)} deliberately.");
    }

    /// <summary>
    /// The coupling that justifies the promotion, asserted directly rather than inferred.
    /// </summary>
    /// <remarks>
    /// <see cref="TheProjectionBoundaryIsPublic"/> would still pass if someone changed
    /// <c>Project</c> to stop taking a <see cref="FormLayout"/> — at which point the layout
    /// records would be public for no surviving reason and this file's entire rationale would
    /// be stale without anything failing. This is the assertion no analyzer duplicates.
    /// </remarks>
    [Fact]
    public void TheProjectionEntryPoint_ExposesFormLayoutInItsPublicSignature()
    {
        var candidates = typeof(WorkItemDetailProjector)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(WorkItemDetailProjector.Project))
            .ToList();

        // Not GetMethod: a second overload would throw AmbiguousMatchException, whose message
        // reads like a bug in this test rather than the boundary event it actually is.
        candidates.Count.ShouldBe(
            1,
            "WorkItemDetailProjector.Project is the single public projection entry point. "
            + "An overload is a boundary change, not a detail — decide it deliberately.");

        var parameters = candidates[0].GetParameters();
        parameters.ShouldNotBeEmpty("Project must accept the layout it projects.");

        // The accessibility rule is about appearing in a public signature at all, not about
        // parameter passing — FallbackFormLayout.For proves the same point via its RETURN
        // type (CS0050). The first parameter is simply where this contract states it.
        parameters[0].ParameterType.ShouldBe(
            typeof(FormLayout),
            "FormLayout appears in a public signature — that is precisely why it cannot be "
            + "internal. If this changed, re-examine whether the promotion is still earned.");
    }

    /// <summary>
    /// The other half of Implementation Decision 9, which AB#253 did NOT overturn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decision 9 named two types in one sentence and ranked them for later promotion:
    /// <i>"If only one is promoted later, promote the rule type first."</i> Reality went the
    /// other way — the layout type was promoted and the rule type was not — because the
    /// promotion was driven by a consumer that materialized rather than by the ranking. The
    /// ranking is therefore stale as a prediction, but the rule types' actual visibility
    /// still matches Decision 9 and is pinned here so promoting them is a deliberate act.
    /// </para>
    /// <para>
    /// <b>All four are listed because the family moves together or not at all.</b>
    /// <c>ProcessRule</c>'s constructor exposes <c>RuleCondition</c>, <c>RuleAction</c> and
    /// <c>RuleCustomization</c>, so publicising it alone does not compile — measured, six
    /// <c>CS0051</c>s. Pinning a subset would imply the rest were considered and excluded.
    /// </para>
    /// </remarks>
    public static TheoryData<Type> RuleTypesCoveredByDecision9() =>
    [
        typeof(ProcessRule),
        typeof(RuleCondition),
        typeof(RuleAction),
        typeof(RuleCustomization),
        typeof(RuleCustomizationKind),
    ];

    [Theory]
    [MemberData(nameof(RuleTypesCoveredByDecision9))]
    public void RuleTypesStayInternalPerDecision9(Type ruleType)
    {
        // typeof rather than a string name: Twig.Domain.Tests holds InternalsVisibleTo, so
        // these compile while internal AND would still compile if promoted — the assertion
        // stays honest either way, and a rename is caught by the compiler instead of
        // surfacing as a confusing TypeLoadException at runtime.
        ruleType.IsVisible.ShouldBeFalse(
            $"{ruleType.Name} stays internal per Implementation Decision 9. AB#253 narrowed "
            + "that decision to the rule types only; it did not promote them.");
    }
}
