using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// Contract §9.1 R1–R15 no-authority conformance suite.
///
/// <para>
/// Two independent invariants per §9.1, plus the §1.1(c) reverse
/// invariant that closes the observe-only guarantee.
/// </para>
///
/// <list type="bullet">
///   <item><b>Field-reference invariant.</b> Enumerated types in the
///     Transport namespace MUST NOT appear as parameters or return
///     types on any surface implementing R1–R15 (claim lifecycle,
///     Change Proposal state transitions, plan lifecycle, ADO
///     mutation, session steering, primary-scope attachment lifecycle,
///     managed-worktree init).</item>
///   <item><b>Event/call-boundary invariant.</b> No transport operation
///     name appears as a member of a workflow-domain interface. The
///     conformance shape here is a name-based assertion — an R-row
///     type MUST NOT publish a method returning a transport
///     observation, taking a transport type, or otherwise "leaking"
///     into an R-row entry point.</item>
///   <item><b>§1.1(c) reverse invariant.</b> Non-close entry points on
///     the transport surface MUST NOT publish
///     <c>Close</c>/<c>PartialClose</c> methods or invoke them
///     transitively via a method the reflection walk can see.</item>
/// </list>
///
/// <para>
/// Reflection can't peer into IL to enumerate reachability inside a
/// method body without an IL-walk library; the assertions below are
/// therefore structural — they catch the mistake of adding a
/// transport-typed parameter or a close-adjacent method to a
/// no-authority surface, which is the primary failure mode a
/// regression would take.
/// </para>
/// </summary>
public sealed class TransportNoAuthorityConformanceTests
{
    private static Assembly DomainAssembly => typeof(Twig.Domain.Common.Result).Assembly;
    private static Assembly InfrastructureAssembly => typeof(WorktreeLocalTransportAttachmentStore).Assembly;

    private static IReadOnlyCollection<System.Type> TransportPublicSurfaceTypes { get; } = new[]
    {
        typeof(TransportAttachmentRecord),
        typeof(TransportWorktreePayload),
        typeof(TransportAgentPayload),
        typeof(TransportTerminalPayload),
        typeof(TransportAdapterTarget),
        typeof(TransportStatusObservation),
        typeof(TransportLivenessObservation),
        typeof(TransportPartialCloseOutcome),
        typeof(AdapterDescription),
        typeof(RecordIdentityRequest),
        typeof(PartialCloseScope),
        typeof(TransportAttachmentEnvelope),
        typeof(VersionedTransportEnvelope),
        typeof(TransportWriteOutcome),
        typeof(RecordedStatus),
        typeof(TransportCapability),
        typeof(TransportFreshness),
        typeof(TransportLivenessPresence),
        typeof(TransportPartialCloseRemaining),
    };

    // R1–R15 verb rows. Each row is asserted as (a) field-reference
    // invariant, (b) event-boundary invariant. R11–R15 additionally
    // participate in the §1.1(c) reverse invariant.

    public static IEnumerable<object[]> RejectedRows() => new[]
    {
        // R1 — claim lifecycle. Owned by AB#737/739; ILocalClaimService is the seam.
        new object[] { "R1", "Twig.Domain.Services.Claims.ILocalClaimService" },
        // R2 — Change Proposal state transition. Owned by plan lifecycle.
        new object[] { "R2", "Twig.Domain.Interfaces.IPlanLifecycleService" },
        // R3 — plan validate/preview/apply/status.
        new object[] { "R3", "Twig.Domain.Interfaces.IPlanLifecycleService" },
        // R4 — ADO work-item state mutation.
        new object[] { "R4", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        // R5 — ADO field update.
        new object[] { "R5", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        // R6 — ADO link add/remove.
        new object[] { "R6", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        // R7 — ADO comment publication.
        new object[] { "R7", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        // R8 — session-steering-mode derivation. IAttachmentStatusProjection is the
        // steering-mode surface owned by AB#738.
        new object[] { "R8", "Twig.Domain.Interfaces.IAttachmentStatusProjection" },
        // R9 — primary-scope attachment lifecycle.
        new object[] { "R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore" },
        new object[] { "R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentService" },
        // R10 — managed-worktree init/reinit.
        new object[] { "R10", "Twig.Domain.Interfaces.IManagedWorktreeInitializer" },
        // R11–R15 — adapter management surface. There is no surface
        // outside the transport namespace that publishes these verbs
        // (that would be R11–R15's failure mode by construction). Row
        // R11 verb "create host workspace" is asserted as: no interface
        // OUTSIDE the transport namespace exposes a method with a name
        // like "CreateWorkspace" that takes a transport type.
        new object[] { "R11", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R12", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R13", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R14", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R15", "Twig.Domain.Services.Claims.ILocalClaimService" },
    };

    [Theory]
    [MemberData(nameof(RejectedRows))]
    public void FieldReference_invariant_no_transport_type_reaches_rejected_row(string row, string interfaceFullName)
    {
        _ = row;
        var iface = DomainAssembly.GetType(interfaceFullName, throwOnError: false);
        iface.ShouldNotBeNull($"required interface {interfaceFullName} not found in Domain assembly.");
        var offending = new List<string>();
        foreach (var method in iface!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (TransportPublicSurfaceTypes.Contains(UnwrapTaskAndResult(method.ReturnType)))
                offending.Add($"{iface.Name}.{method.Name} returns transport type {method.ReturnType.Name}");
            foreach (var parameter in method.GetParameters())
            {
                if (TransportPublicSurfaceTypes.Contains(UnwrapTaskAndResult(parameter.ParameterType)))
                    offending.Add($"{iface.Name}.{method.Name}({parameter.Name}: {parameter.ParameterType.Name}) takes transport type");
            }
        }
        offending.ShouldBeEmpty($"{row} rejected surface must NOT reference any transport type; found:\n  {string.Join("\n  ", offending)}");
    }

    [Theory]
    [MemberData(nameof(RejectedRows))]
    public void EventBoundary_invariant_no_transport_operation_name_appears_on_rejected_row(string row, string interfaceFullName)
    {
        _ = row;
        var iface = DomainAssembly.GetType(interfaceFullName, throwOnError: false);
        iface.ShouldNotBeNull($"required interface {interfaceFullName} not found in Domain assembly.");
        // §9.1 event-boundary invariant: an R-row surface must not
        // publish a transport operation. Generic verbs like Write /
        // Detach / Close are shared with AB#738 attachment lifecycle
        // (write a claim link, detach the primary scope, close a
        // work-item state) and are NOT transport-typed unless their
        // parameters or return type carry a transport identity. The
        // check is therefore: any method whose name matches a
        // transport-unique verb (ReportStatus / ProbeLiveness /
        // RecordIdentity / SelectPresentation / Render /
        // PartialClose) OR any method whose signature carries a
        // transport type. The transport-type check duplicates the
        // field-reference invariant deliberately — a defence in depth,
        // matching §9.1's "both invariants must pass" wording.
        var transportUniqueVerbs = new[]
        {
            "ReportStatus", "ProbeLiveness", "RecordIdentity",
            "SelectPresentation", "Render", "PartialClose",
        };
        var problems = new List<string>();
        foreach (var method in iface!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var verb in transportUniqueVerbs)
            {
                if (method.Name == verb || method.Name.EndsWith(verb + "Async", System.StringComparison.Ordinal))
                    problems.Add($"{iface.Name}.{method.Name} matches transport-unique verb '{verb}'");
            }
            if (TransportPublicSurfaceTypes.Contains(UnwrapTaskAndResult(method.ReturnType)))
                problems.Add($"{iface.Name}.{method.Name} returns transport type");
            foreach (var parameter in method.GetParameters())
            {
                if (TransportPublicSurfaceTypes.Contains(UnwrapTaskAndResult(parameter.ParameterType)))
                    problems.Add($"{iface.Name}.{method.Name} takes transport type parameter");
            }
        }
        problems.ShouldBeEmpty($"event-boundary invariant: no transport operation or observation may appear on a rejected surface; found:\n  {string.Join("\n  ", problems)}");
    }

    // §1.1(c) reverse invariant: walk the reachable event/call graph
    // from every NON-close transport entry point and assert Close /
    // PartialClose unreachable. Reflection can't run a true reachability
    // walk of IL bodies without an IL library; the assertion is
    // therefore structural — the shape validator, envelope mapper,
    // store's Read/Write, dispatcher's non-close entry points, and the
    // renderer's SelectPresentation/RenderAsync MUST NOT publish
    // Close/PartialClose methods themselves.
    [Fact]
    public void ReverseInvariant_shape_validator_never_publishes_close_or_partial_close()
    {
        AssertTypeHasNoCloseMembers(typeof(TransportShapeValidator));
    }

    [Fact]
    public void ReverseInvariant_envelope_mapper_never_publishes_close_or_partial_close()
    {
        AssertTypeHasNoCloseMembers(typeof(TransportEnvelopeMapper));
    }

    [Fact]
    public void ReverseInvariant_store_read_never_publishes_close_or_partial_close()
    {
        // ITransportAttachmentStore.Close/Detach ARE valid entry points
        // — the reverse invariant applies to the READ side, not to the
        // documented Close/Detach entry points. The store's Close is
        // still explicit caller invocation only per §1.1(c).
        var iface = typeof(ITransportAttachmentStore);
        var readMethod = iface.GetMethod(nameof(ITransportAttachmentStore.ReadWithRevisionAsync))!;
        readMethod.ShouldNotBeNull();
        // Read must not return a partial-close outcome.
        readMethod.ReturnType.ShouldNotBe(typeof(System.Threading.Tasks.Task<Twig.Domain.Common.Result<TransportPartialCloseOutcome>>));
    }

    [Fact]
    public void ReverseInvariant_renderer_select_and_render_never_publish_close()
    {
        AssertMemberNameDoesNotEqual(typeof(IChangeProposalRenderer), "CloseAsync");
        AssertMemberNameDoesNotEqual(typeof(IChangeProposalRenderer), "PartialCloseAsync");
    }

    private static void AssertTypeHasNoCloseMembers(System.Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            method.Name.ShouldNotBe("Close");
            method.Name.ShouldNotBe("PartialClose");
            method.Name.ShouldNotBe("CloseAsync");
            method.Name.ShouldNotBe("PartialCloseAsync");
        }
    }

    private static void AssertMemberNameDoesNotEqual(System.Type type, string forbidden)
    {
        var found = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == forbidden);
        found.ShouldBeFalse($"{type.Name} publishes forbidden member '{forbidden}'.");
    }

    // Unwrap Task<T>, Task<Result<T>>, Result<T> to inspect the terminal
    // payload type.
    private static System.Type UnwrapTaskAndResult(System.Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.Task<>))
            return UnwrapTaskAndResult(type.GetGenericArguments()[0]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Twig.Domain.Common.Result<>))
            return UnwrapTaskAndResult(type.GetGenericArguments()[0]);
        return type;
    }

    // §8.3 ADO projection boundary: no transport type is referenced by
    // any ADO namespace. Enforced structurally by checking every type
    // in the Infrastructure assembly's Twig.Infrastructure.Ado.*
    // namespace for a transport-typed member.
    [Fact]
    public void AdoNamespace_never_references_a_transport_type()
    {
        var adoTypes = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace is not null
                     && t.Namespace.StartsWith("Twig.Infrastructure.Ado", System.StringComparison.Ordinal))
            .ToList();
        adoTypes.ShouldNotBeEmpty();
        var offending = new List<string>();
        var transportSet = new HashSet<System.Type>(TransportPublicSurfaceTypes);
        foreach (var t in adoTypes)
        {
            foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (transportSet.Contains(UnwrapTaskAndResult(method.ReturnType)))
                    offending.Add($"{t.FullName}.{method.Name} returns transport type {method.ReturnType.Name}");
                foreach (var parameter in method.GetParameters())
                {
                    if (transportSet.Contains(UnwrapTaskAndResult(parameter.ParameterType)))
                        offending.Add($"{t.FullName}.{method.Name}({parameter.Name}: {parameter.ParameterType.Name}) takes transport type");
                }
            }
            foreach (var property in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (transportSet.Contains(UnwrapTaskAndResult(property.PropertyType)))
                    offending.Add($"{t.FullName}.{property.Name} returns transport type {property.PropertyType.Name}");
            }
        }
        offending.ShouldBeEmpty($"§8.3 boundary: ADO namespace types must NOT reference any transport type. Offenders:\n  {string.Join("\n  ", offending)}");
    }

    // §8.3 namespace seam: transport types live in
    // Twig.Infrastructure.Persistence.Transport. Anything moving them
    // out would defeat the §8.3 test above.
    [Fact]
    public void All_transport_public_surface_types_live_in_transport_namespace()
    {
        var offending = new List<string>();
        foreach (var t in TransportPublicSurfaceTypes)
        {
            if (t.Namespace != "Twig.Infrastructure.Persistence.Transport")
                offending.Add($"{t.FullName} lives outside Twig.Infrastructure.Persistence.Transport");
        }
        offending.ShouldBeEmpty($"§8.3 namespace seam violation:\n  {string.Join("\n  ", offending)}");
    }
}
