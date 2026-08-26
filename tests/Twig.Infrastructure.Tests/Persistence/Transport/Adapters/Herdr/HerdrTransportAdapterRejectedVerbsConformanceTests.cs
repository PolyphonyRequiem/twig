using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// AB#746 conformance suite for the §9.1 R1–R15 rejected-verb rows,
/// applied specifically to the Herdr adapter surface. This test proves
/// the adapter class (a) publishes NO method whose name matches any
/// R1–R15 verb (or any of Herdr's specifically-banned management verbs
/// per §1.1(b)) and (b) takes NO constructor dependency on the
/// workflow-domain interfaces §9.1 enumerates as rejected sinks.
///
/// <para>
/// The existing <c>TransportNoAuthorityConformanceTests</c> asserts the
/// same R1–R15 matrix from the transport-neutral side (no domain seam
/// takes a transport type). This test asserts the mirror invariant
/// from the adapter side: no rejected verb leaks INTO the adapter's
/// surface, and no rejected sink is reachable from a constructor
/// argument. Together the two suites walk both directions of §9.1's
/// event-boundary invariant, matching §12.2's acceptance criterion "a
/// conformance test against the §9.1 rejected-verb rows R1–R15 passes
/// for this adapter."
/// </para>
/// </summary>
public sealed class HerdrTransportAdapterRejectedVerbsConformanceTests
{
    /// <summary>Names of methods that would violate an R-row if
    /// published from the adapter surface. §1.1(b) explicitly forbids
    /// Herdr's five management verbs (focus, rename, resize/zoom,
    /// prompt, start); §9.1 R11–R15 encode the same ban.</summary>
    public static IEnumerable<object[]> RejectedVerbNames() =>
    [
        // R1 — claim lifecycle.
        ["R1", "MintClaim"],
        ["R1", "ActivateClaim"],
        ["R1", "ReleaseClaim"],
        ["R1", "RetireClaim"],
        // R2 — Change Proposal state transition.
        ["R2", "SubmitProposal"],
        ["R2", "AcceptProposal"],
        ["R2", "RejectProposal"],
        // R3 — plan validate/preview/apply/status.
        ["R3", "ValidatePlan"],
        ["R3", "PreviewPlan"],
        ["R3", "ApplyPlan"],
        ["R3", "PlanStatus"],
        // R4 — ADO work-item state mutation.
        ["R4", "TransitionState"],
        ["R4", "MutateState"],
        // R5 — ADO field update.
        ["R5", "UpdateField"],
        // R6 — ADO link add/remove.
        ["R6", "AddLink"],
        ["R6", "RemoveLink"],
        // R7 — ADO comment publication.
        ["R7", "PublishComment"],
        ["R7", "PostNote"],
        // R8 — session-steering-mode derivation.
        ["R8", "DeriveSteeringMode"],
        ["R8", "ResolveSteeringMode"],
        // R9 — primary-scope attachment lifecycle.
        ["R9", "AttachPrimaryScope"],
        ["R9", "RetirePrimaryScope"],
        // R10 — managed-worktree init/reinit.
        ["R10", "InitManagedWorktree"],
        ["R10", "ReinitManagedWorktree"],
        // R11 — CREATE host surfaces.
        ["R11", "CreateWorkspace"],
        ["R11", "CreateTab"],
        ["R11", "CreatePane"],
        ["R11", "CreateAgentSession"],
        ["R11", "CreateWindow"],
        // R12 — focus / bring-to-front (Herdr `focus`).
        ["R12", "Focus"],
        ["R12", "BringToFront"],
        // R13 — rename (Herdr `rename`).
        ["R13", "Rename"],
        // R14 — resize / zoom / move / layout.
        ["R14", "Resize"],
        ["R14", "Zoom"],
        ["R14", "Move"],
        ["R14", "Layout"],
        // R15 — prompt / start / spawn.
        ["R15", "Prompt"],
        ["R15", "Start"],
        ["R15", "Spawn"],
    ];

    /// <summary>Workflow-domain interfaces §9.1 lists as rejected
    /// sinks. Any of these appearing as a constructor argument on the
    /// adapter class would be an R-row violation: the adapter would be
    /// able to reach the sink through DI.</summary>
    public static IEnumerable<object[]> RejectedConstructorDependencies() =>
    [
        // R1 / R11–R15 — claim lifecycle sink.
        ["R1", "Twig.Domain.Services.Claims.ILocalClaimService"],
        // R2 / R3 — plan lifecycle sink.
        ["R2", "Twig.Domain.Interfaces.IPlanLifecycleService"],
        // R4–R7 — ADO mutation sinks.
        ["R4", "Twig.Domain.Interfaces.IAdoWorkItemService"],
        // R8 — session-steering-mode surface owned by AB#738.
        ["R8", "Twig.Domain.Interfaces.IAttachmentStatusProjection"],
        // R9 — primary-scope attachment lifecycle sinks.
        ["R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore"],
        // R10 — managed-worktree init sink.
        ["R10", "Twig.Domain.Interfaces.IManagedWorktreeInitializer"],
    ];

    [Theory]
    [MemberData(nameof(RejectedVerbNames))]
    public void HerdrTransportAdapter_publishes_no_method_matching_a_rejected_R1_R15_verb(string row, string verb)
    {
        _ = row;
        AssertNoMemberMatchingVerb(typeof(HerdrTransportAdapter), verb);
    }

    [Theory]
    [MemberData(nameof(RejectedVerbNames))]
    public void IHerdrHostSurface_publishes_no_method_matching_a_rejected_R1_R15_verb(string row, string verb)
    {
        _ = row;
        AssertNoMemberMatchingVerb(typeof(IHerdrHostSurface), verb);
    }

    [Theory]
    [MemberData(nameof(RejectedConstructorDependencies))]
    public void HerdrTransportAdapter_ctor_never_takes_a_rejected_workflow_domain_dependency(string row, string typeName)
    {
        _ = row;
        var domainAssembly = typeof(Twig.Domain.Common.Result).Assembly;
        var rejectedType = domainAssembly.GetType(typeName, throwOnError: false);
        rejectedType.ShouldNotBeNull($"required interface {typeName} not found in Domain assembly.");
        var ctors = typeof(HerdrTransportAdapter).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var offending = new List<string>();
        foreach (var ctor in ctors)
        {
            foreach (var parameter in ctor.GetParameters())
            {
                if (parameter.ParameterType == rejectedType)
                    offending.Add($"{ctor.Name}({parameter.Name}: {parameter.ParameterType.Name})");
            }
        }
        offending.ShouldBeEmpty($"HerdrTransportAdapter constructor must not accept a rejected dependency; found:\n  {string.Join("\n  ", offending)}");
    }

    /// <summary>
    /// §12.2 the adapter MUST NOT declare a <c>LifecycleFacets</c>
    /// capability beyond the six-value core vocabulary (deferred per
    /// §4.4). Capabilities set MUST be exactly the five §3.3 optional
    /// names; any other implies a schema-change escape.
    /// </summary>
    [Fact]
    public void Capabilities_declared_exactly_match_the_v1_optional_catalogue()
    {
        var adapter = new HerdrTransportAdapter(
            host: new FakeHerdrHostSurface(),
            clock: TimeProvider.System);
        adapter.Capabilities.Count.ShouldBe(5);
        adapter.Capabilities.ShouldContain(TransportCapability.StatusReporting);
        adapter.Capabilities.ShouldContain(TransportCapability.LivenessProbe);
        adapter.Capabilities.ShouldContain(TransportCapability.Detach);
        adapter.Capabilities.ShouldContain(TransportCapability.Close);
        adapter.Capabilities.ShouldContain(TransportCapability.PartialClose);
    }

    /// <summary>
    /// §7.1 / §12.2 — the adapter class MUST implement exactly the
    /// <see cref="ITransportAdapter"/> surface (2 props + 7 methods) and
    /// nothing else public. An extra public method would be an escape
    /// hatch a §9.1 violation could reach through.
    /// </summary>
    [Fact]
    public void HerdrTransportAdapter_publishes_only_the_ITransportAdapter_surface()
    {
        var publicMethods = typeof(HerdrTransportAdapter)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // exclude property getters, Equals, GetHashCode
            .Select(m => m.Name)
            .ToHashSet(System.StringComparer.Ordinal);
        var expected = new HashSet<string>(System.StringComparer.Ordinal)
        {
            nameof(ITransportAdapter.RecordIdentity),
            nameof(ITransportAdapter.DescribeAdapter),
            nameof(ITransportAdapter.ReportStatusAsync),
            nameof(ITransportAdapter.ProbeLivenessAsync),
            nameof(ITransportAdapter.DetachAsync),
            nameof(ITransportAdapter.CloseAsync),
            nameof(ITransportAdapter.PartialCloseAsync),
        };
        publicMethods.ShouldBe(expected, ignoreOrder: true);
    }

    private static void AssertNoMemberMatchingVerb(System.Type type, string verb)
    {
        var offending = new List<string>();
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.Name == verb || method.Name == verb + "Async")
                offending.Add($"{type.Name}.{method.Name}");
        }
        offending.ShouldBeEmpty($"{type.Name} publishes forbidden verb '{verb}'; found:\n  {string.Join("\n  ", offending)}");
    }
}
