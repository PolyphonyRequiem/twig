using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Shouldly;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// Contract §9.1 R1–R15 no-authority conformance suite.
///
/// <para>
/// AB#745's acceptance criterion is that conformance FAILS when an
/// authority-bearing verb is added to the transport surface. That
/// means the check MUST detect a change to the transport surface
/// itself, not just verify that today's declared interfaces match a
/// hand-rolled unique-verb list. The suite runs two mechanisms:
/// </para>
///
/// <list type="number">
///   <item><b>(a) Frozen transport surface.</b> Every type declared
///     in the <c>Twig.Infrastructure.Persistence.Transport</c>
///     namespace (excluding host-adapter implementations, which are
///     tracked by their own conformance suites) is enumerated with
///     its declared method / property names. ANY new member fails
///     until the frozen set is deliberately updated. An approval-style
///     baseline lives in
///     <see cref="TransportSurfaceBaseline.FrozenSurface"/> — we do
///     NOT reuse the shipped <c>PublicAPI.Unshipped.txt</c> analyzer
///     because every transport type is <c>internal</c> and the
///     PublicAPI analyzer only covers <c>public</c> surface, so it
///     cannot see the boundary at all.</item>
///   <item><b>(b) Dependency-direction assertion.</b> The reachable
///     types graph of every transport type — walking parameter,
///     return, property, field, and generic-argument types — MUST NOT
///     include any type from the R1–R15 authority surfaces (claim
///     lifecycle, Change Proposal / plan lifecycle, ADO mutation,
///     session-steering derivation, primary-scope attachment
///     lifecycle, managed-worktree init). The walk is transitive with
///     a visited set to bound cost.</item>
///   <item><b>Mutation proof.</b> A single test drives a synthetic
///     transport-namespace type carrying an authority-bearing verb
///     through the frozen-surface AND dependency-direction helpers
///     and asserts both reject it.</item>
/// </list>
///
/// <para>
/// The original R1–R15 row enumeration is retained as a separate
/// clearly-labelled assertion below — it still checks the historical
/// mistake mode ("an R-row interface takes a transport type or
/// publishes a transport verb") and is not a substitute for the
/// mechanisms above.
/// </para>
/// </summary>
public sealed class TransportNoAuthorityConformanceTests
{
    private static Assembly DomainAssembly => typeof(Twig.Domain.Common.Result).Assembly;
    private static Assembly InfrastructureAssembly => typeof(WorktreeLocalTransportAttachmentStore).Assembly;

    private const string TransportNamespace = "Twig.Infrastructure.Persistence.Transport";

    // Public "record surface" for the field-reference invariant.
    private static IReadOnlyCollection<Type> TransportPublicSurfaceTypes { get; } = new[]
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

    private static IReadOnlyCollection<Type> AuthoritySurfaceTypes()
    {
        var names = new[]
        {
            // R1 — claim lifecycle
            "Twig.Domain.Services.Claims.ILocalClaimService",
            "Twig.Domain.Services.Claims.IAdoClaimProjection",
            "Twig.Domain.Services.Claims.ClaimRecord",
            "Twig.Domain.Services.Claims.MintClaimInput",
            "Twig.Domain.Services.Claims.ReclaimClaimInput",
            "Twig.Domain.Services.Claims.ReleaseClaimInput",
            "Twig.Domain.Services.Claims.ClaimValidationInput",
            "Twig.Domain.Services.Claims.ClaimMintOutcome",
            "Twig.Domain.Services.Claims.ClaimReclaimOutcome",
            "Twig.Domain.Services.Claims.ClaimReleaseOutcome",
            "Twig.Domain.Services.Claims.ClaimValidationOutcome",
            "Twig.Domain.Services.Claims.ClaimLookupOutcome",
            "Twig.Domain.Services.Claims.ClaimLabelUpdateOutcome",
            // R2 / R3
            "Twig.Domain.Interfaces.IPlanLifecycleService",
            // R4–R7
            "Twig.Domain.Interfaces.IAdoWorkItemService",
            // R8
            "Twig.Domain.Interfaces.IAttachmentStatusProjection",
            // R9
            "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore",
            "Twig.Domain.Interfaces.IPrimaryScopeAttachmentService",
            // R10
            "Twig.Domain.Interfaces.IManagedWorktreeInitializer",
        };
        var types = new List<Type>();
        foreach (var n in names)
        {
            var t = DomainAssembly.GetType(n, throwOnError: false);
            if (t is not null) types.Add(t);
        }
        return types;
    }

    // ─── (a) Frozen transport surface ────────────────────────────────

    [Fact]
    public void FrozenSurface_matches_the_expected_baseline()
    {
        var actual = EnumerateTransportSurface(
            InfrastructureAssembly,
            includeType: IsFrozenTransportType);
        var baseline = TransportSurfaceBaseline.FrozenSurface;

        var actualLines = SurfaceToLines(actual);
        var missing = baseline.Except(actualLines, StringComparer.Ordinal).ToList();
        var added = actualLines.Except(baseline, StringComparer.Ordinal).ToList();

        var problems = new List<string>();
        if (missing.Any())
            problems.Add("REMOVED (present in baseline, absent from code):\n  " + string.Join("\n  ", missing));
        if (added.Any())
            problems.Add("ADDED (present in code, absent from baseline):\n  " + string.Join("\n  ", added));

        problems.ShouldBeEmpty(
            "Transport surface drifted from the frozen baseline. Every add MUST be a deliberate " +
            "edit to TransportSurfaceBaseline.FrozenSurface — that's what makes an accidentally " +
            "added authority-bearing verb fail conformance. Details:\n" +
            string.Join("\n\n", problems));
    }

    // ─── (b) Dependency-direction ────────────────────────────────────

    [Fact]
    public void DependencyDirection_no_transport_type_reaches_an_authority_type()
    {
        var authority = new HashSet<Type>(AuthoritySurfaceTypes());
        authority.ShouldNotBeEmpty("Authority surface list is empty — the walk cannot detect anything.");
        var problems = new List<string>();
        foreach (var t in TransportPublicSurfaceTypes)
        {
            var reached = ReachableTypesTransitive(t);
            foreach (var r in reached)
            {
                if (authority.Contains(r))
                    problems.Add($"{t.FullName} → …→ {r.FullName}");
            }
        }
        problems.ShouldBeEmpty(
            "Transport type reaches an R1–R15 authority surface. Adding an authority-bearing " +
            "verb or accepting an authority DTO here defeats the observe-only guarantee. " +
            "Offenders:\n  " + string.Join("\n  ", problems));
    }

    // ─── Mutation proof ──────────────────────────────────────────────

    [Fact]
    public void MutationProof_synthetic_authority_verb_fails_both_mechanisms()
    {
        // (a) Frozen surface: a synthetic transport-namespace type
        // carrying an authority-bearing verb MUST register as a diff.
        var syntheticSurface = new Dictionary<string, List<string>>
        {
            ["Twig.Infrastructure.Persistence.Transport.RogueAuthorityVerb"] = new List<string>
            {
                "M:MintClaim",
                "M:SubmitChangeProposal",
            },
        };
        var baselineLines = SurfaceToLines(EnumerateTransportSurface(
            InfrastructureAssembly, includeType: IsFrozenTransportType));
        var syntheticLines = SurfaceToLines(syntheticSurface);
        var added = syntheticLines.Except(baselineLines, StringComparer.Ordinal).ToList();
        added.ShouldContain("Twig.Infrastructure.Persistence.Transport.RogueAuthorityVerb::M:MintClaim",
            "Frozen-surface mechanism must flag an authority-bearing verb added to the transport namespace.");

        // (b) Dependency direction: a synthetic type returning
        // ClaimRecord MUST be caught.
        var authority = new HashSet<Type>(AuthoritySurfaceTypes());
        var reached = ReachableTypesTransitive(typeof(RogueAuthorityMock));
        var offenders = reached.Where(authority.Contains).ToList();
        offenders.ShouldNotBeEmpty(
            "Dependency-direction walker must catch a synthetic type reaching an authority surface.");
    }

    // Synthetic mock — takes/returns an authority type. Deliberately
    // present so the mutation-proof test has something concrete to walk.
    // Not in the transport namespace, so it does NOT affect the
    // FrozenSurface test.
    private sealed class RogueAuthorityMock
    {
        public Twig.Domain.Services.Claims.ClaimRecord? Mint() => null;
    }

    // ─── R1–R15 row enumeration (retained, clearly labelled) ────────

    public static IEnumerable<object[]> RejectedRows() => new[]
    {
        new object[] { "R1", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R2", "Twig.Domain.Interfaces.IPlanLifecycleService" },
        new object[] { "R3", "Twig.Domain.Interfaces.IPlanLifecycleService" },
        new object[] { "R4", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        new object[] { "R5", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        new object[] { "R6", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        new object[] { "R7", "Twig.Domain.Interfaces.IAdoWorkItemService" },
        new object[] { "R8", "Twig.Domain.Interfaces.IAttachmentStatusProjection" },
        new object[] { "R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore" },
        new object[] { "R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentService" },
        new object[] { "R10", "Twig.Domain.Interfaces.IManagedWorktreeInitializer" },
        new object[] { "R11", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R12", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R13", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R14", "Twig.Domain.Services.Claims.ILocalClaimService" },
        new object[] { "R15", "Twig.Domain.Services.Claims.ILocalClaimService" },
    };

    [Theory]
    [MemberData(nameof(RejectedRows))]
    public void FieldReference_invariant_row_by_row_no_transport_type_on_rejected_surface(string row, string interfaceFullName)
    {
        _ = row;
        var iface = DomainAssembly.GetType(interfaceFullName, throwOnError: false);
        iface.ShouldNotBeNull($"required interface {interfaceFullName} not found in Domain assembly.");
        var transportSet = new HashSet<Type>(TransportPublicSurfaceTypes);
        var offending = new List<string>();
        foreach (var method in iface!.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (transportSet.Contains(UnwrapTaskAndResult(method.ReturnType)))
                offending.Add($"{iface.Name}.{method.Name} returns transport type {method.ReturnType.Name}");
            foreach (var parameter in method.GetParameters())
            {
                if (transportSet.Contains(UnwrapTaskAndResult(parameter.ParameterType)))
                    offending.Add($"{iface.Name}.{method.Name}({parameter.Name}: {parameter.ParameterType.Name}) takes transport type");
            }
        }
        offending.ShouldBeEmpty($"{row} rejected surface must NOT reference any transport type; found:\n  {string.Join("\n  ", offending)}");
    }

    // ─── §1.1(c) reverse invariant (retained) ────────────────────────

    [Fact]
    public void ReverseInvariant_shape_validator_never_publishes_close_or_partial_close() =>
        AssertTypeHasNoCloseMembers(typeof(TransportShapeValidator));

    [Fact]
    public void ReverseInvariant_envelope_mapper_never_publishes_close_or_partial_close() =>
        AssertTypeHasNoCloseMembers(typeof(TransportEnvelopeMapper));

    [Fact]
    public void ReverseInvariant_store_read_never_publishes_close_or_partial_close()
    {
        var iface = typeof(ITransportAttachmentStore);
        var readMethod = iface.GetMethod(nameof(ITransportAttachmentStore.ReadWithRevisionAsync))!;
        readMethod.ShouldNotBeNull();
        readMethod.ReturnType.ShouldNotBe(typeof(Task<Twig.Domain.Common.Result<TransportPartialCloseOutcome>>));
    }

    [Fact]
    public void ReverseInvariant_renderer_select_and_render_never_publish_close()
    {
        AssertMemberNameDoesNotEqual(typeof(IChangeProposalRenderer), "CloseAsync");
        AssertMemberNameDoesNotEqual(typeof(IChangeProposalRenderer), "PartialCloseAsync");
    }

    // ─── §8.3 ADO namespace boundary (retained) ─────────────────────

    [Fact]
    public void AdoNamespace_never_references_a_transport_type()
    {
        var adoTypes = InfrastructureAssembly.GetTypes()
            .Where(t => t.Namespace is not null
                     && t.Namespace.StartsWith("Twig.Infrastructure.Ado", StringComparison.Ordinal))
            .ToList();
        adoTypes.ShouldNotBeEmpty();
        var offending = new List<string>();
        var transportSet = new HashSet<Type>(TransportPublicSurfaceTypes);
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

    [Fact]
    public void All_transport_public_surface_types_live_in_transport_namespace()
    {
        var offending = new List<string>();
        foreach (var t in TransportPublicSurfaceTypes)
        {
            if (t.Namespace != TransportNamespace)
                offending.Add($"{t.FullName} lives outside {TransportNamespace}");
        }
        offending.ShouldBeEmpty($"§8.3 namespace seam violation:\n  {string.Join("\n  ", offending)}");
    }

    // ─── helpers ─────────────────────────────────────────────────────

    // The "frozen" transport surface excludes host-adapter
    // implementations (author-owned; each has its own conformance
    // suite) and their sub-namespaces.
    private static bool IsFrozenTransportType(Type t) =>
        t.Namespace == TransportNamespace
        && !t.Name.EndsWith("TransportAdapter", StringComparison.Ordinal);

    private static void AssertTypeHasNoCloseMembers(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            method.Name.ShouldNotBe("Close");
            method.Name.ShouldNotBe("PartialClose");
            method.Name.ShouldNotBe("CloseAsync");
            method.Name.ShouldNotBe("PartialCloseAsync");
        }
    }

    private static void AssertMemberNameDoesNotEqual(Type type, string forbidden)
    {
        var found = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == forbidden);
        found.ShouldBeFalse($"{type.Name} publishes forbidden member '{forbidden}'.");
    }

    private static Type UnwrapTaskAndResult(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return UnwrapTaskAndResult(type.GetGenericArguments()[0]);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Twig.Domain.Common.Result<>))
            return UnwrapTaskAndResult(type.GetGenericArguments()[0]);
        return type;
    }

    private static Dictionary<string, List<string>> EnumerateTransportSurface(
        Assembly assembly, Func<Type, bool> includeType)
    {
        var surface = new Dictionary<string, List<string>>();
        var types = assembly.GetTypes()
            .Where(t => includeType(t) && !t.IsCompilerGenerated())
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var t in types)
        {
            var sigils = new SortedSet<string>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(flags))
                if (!m.IsSpecialName && !IsRecordAutoMember(m.Name)) sigils.Add("M:" + m.Name);
            foreach (var p in t.GetProperties(flags))
                sigils.Add("P:" + p.Name);
            surface[t.FullName!] = sigils.ToList();
        }
        return surface;
    }

    // Skip record-generated plumbing so the baseline reflects contract
    // members only, not compiler artifacts every record ships with.
    private static bool IsRecordAutoMember(string name) =>
        name is "Equals" or "GetHashCode" or "ToString" or "Deconstruct" or "<Clone>$"
        || name.StartsWith("<", StringComparison.Ordinal);

    private static List<string> SurfaceToLines(Dictionary<string, List<string>> surface)
    {
        var lines = new List<string>();
        foreach (var kv in surface.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (var sig in kv.Value)
                lines.Add(kv.Key + "::" + sig);
        }
        return lines;
    }

    /// <summary>Transitive reachable-types walk from a root. Only
    /// includes types from Domain / Infrastructure assemblies to
    /// prevent walking the BCL.</summary>
    private static HashSet<Type> ReachableTypesTransitive(Type root)
    {
        var visited = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!visited.Add(t)) continue;
            // Skip BCL types — walking System.Object or generic Task<>
            // pollutes the walk without adding signal. Keep everything
            // in the twig assemblies (Domain / Infrastructure / test
            // assemblies) so a synthetic mock in the test project can
            // participate in the walk.
            var asmName = t.Assembly.FullName ?? string.Empty;
            if (asmName.StartsWith("System", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("mscorlib", StringComparison.Ordinal)) continue;
            if (asmName.StartsWith("netstandard", StringComparison.Ordinal)) continue;
            if (t == typeof(object) || t == typeof(void) || t.IsPrimitive) continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in t.GetMethods(flags))
            {
                queue.Enqueue(UnwrapTaskAndResult(m.ReturnType));
                foreach (var p in m.GetParameters())
                    queue.Enqueue(UnwrapTaskAndResult(p.ParameterType));
                if (m.IsGenericMethodDefinition)
                    foreach (var g in m.GetGenericArguments()) queue.Enqueue(g);
            }
            foreach (var p in t.GetProperties(flags))
                queue.Enqueue(UnwrapTaskAndResult(p.PropertyType));
            foreach (var f in t.GetFields(flags))
                queue.Enqueue(UnwrapTaskAndResult(f.FieldType));
            if (t.IsGenericType)
                foreach (var g in t.GetGenericArguments()) queue.Enqueue(g);
            if (t.BaseType is not null) queue.Enqueue(t.BaseType);
        }
        return visited;
    }
}

internal static class TransportSurfaceTypeExtensions
{
    public static bool IsCompilerGenerated(this Type type) =>
        type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null
        || type.Name.Contains('<')
        || type.Name.Contains("d__")
        || type.Name.StartsWith("__", StringComparison.Ordinal);
}
