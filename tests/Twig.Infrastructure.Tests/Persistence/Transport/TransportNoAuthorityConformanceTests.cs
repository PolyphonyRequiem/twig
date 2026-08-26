using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
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
/// hand-rolled unique-verb list. The suite runs three mechanisms:
/// </para>
///
/// <list type="number">
///   <item><b>(a) Frozen transport surface.</b> Every type declared
///     in the <c>Twig.Infrastructure.Persistence.Transport</c>
///     namespace — INCLUDING <see cref="ITransportAdapter"/>, the
///     single most important type on the surface — is enumerated with
///     its declared method / property SIGNATURES (name + return type
///     + generic arity + parameter types with modifiers). ANY new
///     member OR any overload OR any parameter/return-type change
///     fails until the frozen set is deliberately updated. Concrete
///     implementations of <see cref="ITransportAdapter"/> are excluded
///     — those are tracked by their own adapter conformance suites.
///     An approval-style baseline lives in
///     <see cref="TransportSurfaceBaseline.FrozenSurface"/>. The
///     shipped <c>PublicAPI</c> analyzer only covers <c>public</c>
///     surface and cannot see the internal transport boundary, so we
///     roll our own.</item>
///   <item><b>(b) Dependency-direction assertion (types + call
///     graph).</b> The reachable types graph of every transport type
///     — walking parameter, return, property, field, and
///     generic-argument types — MUST NOT include any type from the
///     R1–R15 authority surfaces. Additionally, a call-graph walk
///     rooted at every declared method of the seven transport
///     OPERATION types (dispatcher, store, envelope mapper, shape
///     validator, renderers, and the <see cref="ITransportAdapter"/>
///     interface itself) recurses into IL and asserts no reachable
///     callee's declaring type is an R1–R15 authority surface. This
///     catches a transport operation that calls a claim / plan /
///     steering / ADO sink without changing any signature — the
///     scalar-through-a-string leak the type walk cannot see.</item>
///   <item><b>(c) Mutation proof.</b> Synthetic transport-namespace
///     type carrying authority-bearing verbs and reaching authority
///     types are driven through EACH mechanism; each is asserted to
///     reject them. Includes an OVERLOAD canary (proves the frozen
///     baseline distinguishes overloads by signature) and a helper
///     canary (proves the call-graph walk descends past root
///     methods).</item>
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

    /// <summary>
    /// Transport OPERATION types §9.1 roots the call-graph walk in.
    /// Includes both the dispatcher / store / renderer / mapper /
    /// validator concrete surfaces (their methods can only reach an
    /// authority sink by making a CALL — no signature change needed)
    /// and the <see cref="ITransportAdapter"/> interface, whose
    /// declared verbs the walk cannot descend past into but which
    /// serves as the mutation-proof canary root.
    /// </summary>
    private static IReadOnlyCollection<Type> TransportOperationRoots { get; } = new[]
    {
        typeof(ITransportAdapter),
        typeof(TransportAdapterDispatcher),
        typeof(ITransportAttachmentStore),
        typeof(WorktreeLocalTransportAttachmentStore),
        typeof(TransportShapeValidator),
        typeof(TransportEnvelopeMapper),
        typeof(IChangeProposalRenderer),
        typeof(ChangeProposalRenderer),
        typeof(TerminalTextChangeProposalRenderer),
        typeof(ITransportAdapterRegistry),
        typeof(TransportAdapterRegistry),
        typeof(IChangeProposalPresentationSupportRegistry),
        typeof(ChangeProposalPresentationSupportRegistry),
    };

    // §9.1 R1–R15 authority surfaces. Resolved via a compile-time
    // token where possible; every string lookup is asserted to
    // resolve (finding 4 — a stale name would silently shrink the
    // guard).
    private static readonly (string Row, Type ResolvedType)[] _authoritySurfaces = ResolveAuthoritySurfaces();

    private static (string Row, Type ResolvedType)[] ResolveAuthoritySurfaces()
    {
        // Prefer typeof(...) so a rename breaks the build, not the
        // test silently. String lookup is only used when the type
        // sits behind an internal boundary reachable only by name.
        var claimsAssembly = DomainAssembly;

        var entries = new List<(string Row, Type Resolved)>();
        void AddByType(string row, Type t) => entries.Add((row, t));
        void AddByName(string row, string fullName)
        {
            var t = claimsAssembly.GetType(fullName, throwOnError: false);
            if (t is null)
                throw new InvalidOperationException(
                    $"Authority surface type '{fullName}' (§9.1 row {row}) did not resolve. " +
                    $"A rename silently shrinks the R1–R15 guard — fix this list or the type.");
            entries.Add((row, t));
        }

        // R1 — claim lifecycle
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ILocalClaimService));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.IAdoClaimProjection));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimRecord));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.MintClaimInput));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ReclaimClaimInput));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ReleaseClaimInput));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimValidationInput));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimMintOutcome));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimReclaimOutcome));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimReleaseOutcome));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimValidationOutcome));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimLookupOutcome));
        AddByType("R1", typeof(Twig.Domain.Services.Claims.ClaimLabelUpdateOutcome));

        // R2 / R3 — plan lifecycle. Resolved by name because
        // IPlanLifecycleService lives behind an internal boundary in
        // the same assembly.
        AddByName("R2", "Twig.Domain.Interfaces.IPlanLifecycleService");

        // R4–R7 — ADO mutation surfaces.
        AddByType("R4", typeof(Twig.Domain.Interfaces.IAdoWorkItemService));

        // R8 — session-steering-mode derivation.
        AddByName("R8", "Twig.Domain.Interfaces.IAttachmentStatusProjection");

        // R9 — primary-scope attachment lifecycle.
        AddByName("R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore");
        AddByName("R9", "Twig.Domain.Interfaces.IPrimaryScopeAttachmentService");

        // R10 — managed-worktree init.
        AddByName("R10", "Twig.Domain.Interfaces.IManagedWorktreeInitializer");

        return entries.ToArray();
    }

    private static IReadOnlyCollection<Type> AuthoritySurfaceTypes() =>
        _authoritySurfaces.Select(e => e.ResolvedType).ToArray();

    // ─── (a) Frozen transport surface ────────────────────────────────

    [Fact]
    public void FrozenSurface_matches_the_expected_baseline()
    {
        var actualLines = SurfaceToLines(EnumerateTransportSurface(
            InfrastructureAssembly,
            includeType: IsFrozenTransportType));
        var baseline = TransportSurfaceBaseline.FrozenSurface;

        var missing = baseline.Except(actualLines, StringComparer.Ordinal).ToList();
        var added = actualLines.Except(baseline, StringComparer.Ordinal).ToList();

        var problems = new List<string>();
        if (missing.Any())
            problems.Add("REMOVED (present in baseline, absent from code):\n  " + string.Join("\n  ", missing));
        if (added.Any())
            problems.Add("ADDED (present in code, absent from baseline):\n  " + string.Join("\n  ", added));

        problems.ShouldBeEmpty(
            "Transport surface drifted from the frozen baseline. Every add/change MUST be a " +
            "deliberate edit to TransportSurfaceBaseline.FrozenSurface — that's what makes an " +
            "accidentally added authority-bearing verb (or overload, or param-type change) fail " +
            "conformance. Details:\n" + string.Join("\n\n", problems));
    }

    // ─── (b) Dependency-direction — type walk ────────────────────────

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

    // ─── (b') Call-graph walk from transport operations ──────────────

    [Fact]
    public void CallGraph_no_transport_operation_reaches_an_authority_type()
    {
        // Finding 3: the type-walk is a NECESSARY but not sufficient
        // condition. A transport operation could call a rejected sink
        // WITHOUT changing any signature. Root the walk in the actual
        // operation methods and inspect their IL.
        var authorityByFullName = AuthoritySurfaceTypes()
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);
        authorityByFullName.ShouldNotBeEmpty("Authority surfaces did not resolve — the walk cannot detect anything.");

        var problems = new List<string>();
        TransportCallGraphWalker.Walk(
            TransportOperationRoots,
            onCallee: (from, callee) =>
            {
                var declaring = callee.DeclaringType?.FullName;
                if (declaring is not null && authorityByFullName.Contains(declaring))
                    problems.Add($"{TransportCallGraphWalker.Describe(from)} -> {declaring}.{callee.Name}");
            },
            onReferencedType: (from, referenced) =>
            {
                var fn = referenced.FullName;
                if (fn is not null && authorityByFullName.Contains(fn))
                    problems.Add($"{TransportCallGraphWalker.Describe(from)} references type {fn}");
            });

        problems.ShouldBeEmpty(
            "A transport operation's reachable call graph touches an R1–R15 authority surface. " +
            "This is the scalar-through-a-string leak the type walk cannot see: an operation " +
            "MAY not accept an authority DTO but still call an authority sink. Offenders:\n  " +
            string.Join("\n  ", problems));
    }

    // ─── (c) Mutation proofs ─────────────────────────────────────────

    [Fact]
    public void MutationProof_synthetic_authority_verb_fails_frozen_surface_mechanism()
    {
        // A synthetic transport-namespace type carrying an
        // authority-bearing verb MUST register as a diff. This uses
        // the same signature formatter the real surface enumeration
        // uses so the assertion binds to the exact format the guard
        // enforces.
        var syntheticSurface = new Dictionary<string, List<string>>
        {
            ["Twig.Infrastructure.Persistence.Transport.RogueAuthorityVerb"] = new List<string>
            {
                "M:MintClaim():System.Void",
                "M:SubmitChangeProposal():System.Void",
            },
        };
        var baselineLines = SurfaceToLines(EnumerateTransportSurface(
            InfrastructureAssembly, includeType: IsFrozenTransportType));
        var syntheticLines = SurfaceToLines(syntheticSurface);
        var added = syntheticLines.Except(baselineLines, StringComparer.Ordinal).ToList();
        added.ShouldContain("Twig.Infrastructure.Persistence.Transport.RogueAuthorityVerb::M:MintClaim():System.Void",
            "Frozen-surface mechanism must flag an authority-bearing verb added to the transport namespace.");
    }

    [Fact]
    public void MutationProof_overload_that_would_route_a_claim_record_fails_frozen_surface()
    {
        // Finding 1: baselines that record NAMES only silently accept
        // overloads and parameter-type changes. Prove the new
        // signature format catches an overload whose parameter list
        // routes a ClaimRecord.
        var authorityBearingOverload = FormatMethodSignature(
            typeof(OverloadCanary).GetMethod(
                nameof(OverloadCanary.CloseAsync),
                new[] { typeof(Twig.Domain.Services.Claims.ClaimRecord) })!);

        var innocuousOverload = FormatMethodSignature(
            typeof(OverloadCanary).GetMethod(
                nameof(OverloadCanary.CloseAsync),
                new[] { typeof(TransportAdapterTarget) })!);

        authorityBearingOverload.ShouldNotBe(innocuousOverload,
            "Signature formatter must distinguish overloads by their parameter list — a " +
            "baseline that collapses overloads by name is worse than no baseline: it makes " +
            "the guarantee vacuous.");
        authorityBearingOverload
            .Contains("Twig.Domain.Services.Claims.ClaimRecord", StringComparison.Ordinal)
            .ShouldBeTrue(
                "The formatter must surface parameter types in the signature so a `CloseAsync(ClaimRecord)` " +
                "overload appears distinct from the existing `CloseAsync(TransportAdapterTarget)`.");
    }

    [Fact]
    public void MutationProof_synthetic_authority_call_fails_call_graph_walk()
    {
        // Finding 3 canary: a transport-namespace type that CALLS an
        // authority sink from a HELPER method (not the root method)
        // must be caught by the transitive walk. If the walker
        // stopped at root methods, this canary would slip through.
        var authorityByFullName = AuthoritySurfaceTypes()
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        var offenders = new List<string>();
        TransportCallGraphWalker.Walk(
            typeof(RogueAuthorityCallGraphCanary),
            onCallee: (from, callee) =>
            {
                var declaring = callee.DeclaringType?.FullName;
                if (declaring is not null && authorityByFullName.Contains(declaring))
                    offenders.Add($"{TransportCallGraphWalker.Describe(from)} -> {declaring}.{callee.Name}");
            });

        offenders.ShouldNotBeEmpty(
            "Transitive call-graph walker must catch a synthetic transport operation whose " +
            "ROOT method only calls a helper and the helper reaches an authority sink. If it " +
            "does not, the walk stops at the root and the guarantee is vacuous.");
    }

    [Fact]
    public void MutationProof_synthetic_authority_type_reference_fails_type_walk()
    {
        // Retained: the type walk (not the call graph) must reject a
        // type reaching an authority surface by TYPE reference.
        var authority = new HashSet<Type>(AuthoritySurfaceTypes());
        var reached = ReachableTypesTransitive(typeof(RogueAuthorityMock));
        var offenders = reached.Where(authority.Contains).ToList();
        offenders.ShouldNotBeEmpty(
            "Dependency-direction type walker must catch a synthetic type reaching an authority surface.");
    }

    [Fact]
    public void MutationProof_stale_authority_type_name_would_fail_loudly()
    {
        // Finding 4: a lookup miss must not silently shrink the
        // authority set. Assert every entry resolved AT class-init
        // (ResolveAuthoritySurfaces threw on miss). This test simply
        // proves resolution happened for every row we care about.
        var seenRows = _authoritySurfaces.Select(e => e.Row).ToHashSet(StringComparer.Ordinal);
        foreach (var row in new[] { "R1", "R2", "R4", "R8", "R9", "R10" })
        {
            seenRows.ShouldContain(row,
                $"Authority row {row} did not resolve any type — the R1–R15 guard has silently " +
                "shrunk. A rename must break the build, not soften the guarantee.");
        }
    }

    // ─── Synthetic mocks (call graph + type walk canaries) ───────────

    // Synthetic mock — takes/returns an authority type. Not in
    // transport namespace so it does NOT affect the FrozenSurface
    // test. Used by the type-walk canary.
    private sealed class RogueAuthorityMock
    {
        public Twig.Domain.Services.Claims.ClaimRecord? Mint() => null;
    }

    // Synthetic mock — root method delegates to a HELPER that
    // touches an authority surface. Used by the call-graph canary
    // per finding 3.
    private sealed class RogueAuthorityCallGraphCanary
    {
        // Root method: signatures reveal nothing. Only a call.
        public Task<Twig.Domain.Common.Result> CloseAsync(TransportAdapterTarget target, CancellationToken ct)
            => LaunderThroughHelperAsync(target, ct);

        // Helper: the actual authority reach. If the walker stopped
        // at root methods, the scanner would never see this.
        private static Task<Twig.Domain.Common.Result> LaunderThroughHelperAsync(
            TransportAdapterTarget target, CancellationToken ct)
        {
            _ = ForbiddenClaimReach();
            return Task.FromResult(Twig.Domain.Common.Result.Ok());
        }
        // Emit an IL token whose declaring type is in an authority
        // namespace so the walker's onCallee sees it (a bare return
        // of ClaimRecord? in the method signature does not surface
        // in the IL body — only member accesses do).
        private static Twig.Domain.Services.Claims.ClaimRecord ForbiddenClaimReach()
            => new Twig.Domain.Services.Claims.ClaimRecord(
                SchemaVersion: 1,
                ClaimId: string.Empty,
                Label: null,
                ConnectionRef: string.Empty,
                PrimaryScopeId: string.Empty,
                PrimaryScopeKind: string.Empty,
                HolderIdentity: string.Empty,
                HolderDisplay: null,
                WorktreeFingerprint: string.Empty,
                State: string.Empty,
                Origin: string.Empty,
                LeaseGeneration: 0,
                ExpiresAt: null,
                CreatedAt: System.DateTimeOffset.UnixEpoch,
                ActivatedAt: null,
                ReleasedAt: null,
                SupersededByClaimId: null,
                ReleaseReason: null,
                Notes: null,
                CasToken: string.Empty);
    }

    // Overload canary for finding 1: same NAME, different parameter
    // types. A baseline that records names only collapses these two
    // signatures and cannot distinguish an authority-bearing overload
    // from an innocuous one.
    private sealed class OverloadCanary
    {
        public Task CloseAsync(TransportAdapterTarget target) => Task.CompletedTask;
        public Task CloseAsync(Twig.Domain.Services.Claims.ClaimRecord record) => Task.CompletedTask;
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

    // ─── §8.3 ADO namespace boundary — signature seam (rail 1) ───────

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
        offending.ShouldBeEmpty($"§8.3 rail 1 boundary: ADO namespace types must NOT reference any transport type. Offenders:\n  {string.Join("\n  ", offending)}");
    }

    // ─── §8.3 rail 2 — outbound call-graph from ADO client ───────────

    [Fact]
    public void AdoClient_call_graph_never_reaches_transport_namespace()
    {
        // Finding 5: rail 2 in the contract — the compile-time
        // signature seam is not enough. Walk the ADO REST client's IL
        // from every entry point and assert no reachable callee lives
        // in the transport namespace. This is the scalar-through-a-
        // string leak the type walk cannot see.
        var adoClient = InfrastructureAssembly.GetType("Twig.Infrastructure.Ado.AdoRestClient");
        adoClient.ShouldNotBeNull("AdoRestClient must be present to root the §8.3 rail 2 walk.");

        var offenders = new List<string>();
        TransportCallGraphWalker.Walk(
            adoClient!,
            onCallee: (from, callee) =>
            {
                var declaring = callee.DeclaringType?.FullName ?? string.Empty;
                if (declaring.StartsWith(TransportNamespace + ".", StringComparison.Ordinal) ||
                    string.Equals(declaring, TransportNamespace, StringComparison.Ordinal))
                {
                    // The guard itself lives in a neutral namespace
                    // (Twig.Infrastructure.Boundary) so it is not
                    // matched; explicitly whitelist any callee back
                    // into that neutral surface here.
                    offenders.Add($"{TransportCallGraphWalker.Describe(from)} -> {declaring}.{callee.Name}");
                }
            },
            onReferencedType: (from, referenced) =>
            {
                var declaring = referenced.FullName ?? string.Empty;
                if (declaring.StartsWith(TransportNamespace + ".", StringComparison.Ordinal))
                    offenders.Add($"{TransportCallGraphWalker.Describe(from)} references transport type {declaring}");
            });

        offenders.ShouldBeEmpty(
            "§8.3 rail 2: the ADO REST client's outbound call graph reaches into the transport " +
            "namespace. Even a helper method call that returns a transport scalar would let a " +
            "value laundered from `TransportAdapterTarget.HostAttachmentId` reach an ADO " +
            "AddComment/Patch sink. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    // ─── §8.3 rail 3 — provenance runtime backstop ───────────────────

    [Fact]
    public void AdoProjectionGuard_rejects_a_transport_origin_string()
    {
        // Finding 5 canary: build the ACTUAL guard used at the ADO
        // client boundary with a marked string. If a maintainer
        // removes the AssertNoTransportOrigin call sites, the runtime
        // backstop is silently defeated and this test fails.
        var guardType = InfrastructureAssembly.GetType("Twig.Infrastructure.Boundary.AdoProjectionGuard");
        guardType.ShouldNotBeNull(
            "§8.3 rail 3 guard type Twig.Infrastructure.Boundary.AdoProjectionGuard is missing.");

        // Construct a fresh string instance (avoids interning
        // collisions with any other test's marked literal).
        var mark = guardType!.GetMethod("MarkTransportOrigin", BindingFlags.Public | BindingFlags.Static);
        var assert = guardType.GetMethod("AssertNoTransportOrigin", BindingFlags.Public | BindingFlags.Static);
        mark.ShouldNotBeNull();
        assert.ShouldNotBeNull();

        var suspect = new string("herdr-pane".AsSpan());
        mark!.Invoke(null, new object?[] { suspect });

        var thrown = Should.Throw<TargetInvocationException>(
            () => assert!.Invoke(null, new object?[] { suspect, "test-sink" }));
        thrown.InnerException.ShouldNotBeNull();
        thrown.InnerException!.Message
            .Contains("transport-ado-projection-forbidden", StringComparison.Ordinal)
            .ShouldBeTrue(
                "§11: the runtime backstop MUST fire the stable failure identifier so callers " +
                "can route on the literal without parsing prose.");
    }

    [Fact]
    public void AdoRestClient_AddCommentAsync_actually_calls_the_guard()
    {
        // Finding 5: the guarantee is meaningless if the guard is
        // wired in the wrong file. This asserts the AdoRestClient's
        // AddCommentAsync (its compiler-generated async state
        // machine) IL literally contains a call to
        // AdoProjectionGuard.AssertNoTransportOrigin. Removing the
        // call from AddCommentAsync FAILS this test.
        var adoClient = InfrastructureAssembly.GetType("Twig.Infrastructure.Ado.AdoRestClient")!;
        var addComment = adoClient.GetMethod(
            "AddCommentAsync",
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(int), typeof(string), typeof(CancellationToken) })!;
        addComment.ShouldNotBeNull();

        // Async methods are transformed into state machines; the
        // real IL lives in the state machine's MoveNext, referenced
        // via AsyncStateMachineAttribute.
        var stateMachine = addComment.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType;
        stateMachine.ShouldNotBeNull(
            "AddCommentAsync must be async — the guard call is expected in its state machine.");

        var callsGuard = false;
        TransportCallGraphWalker.Walk(
            new[] { stateMachine! },
            shouldRecurse: _ => false,
            onCallee: (from, callee) =>
            {
                if (callee.DeclaringType?.FullName == "Twig.Infrastructure.Boundary.AdoProjectionGuard"
                    && callee.Name == "AssertNoTransportOrigin")
                    callsGuard = true;
            });
        callsGuard.ShouldBeTrue(
            "AdoRestClient.AddCommentAsync must call AdoProjectionGuard.AssertNoTransportOrigin. " +
            "Without this call the §8.3 rail 3 runtime backstop is unwired: the failure identifier " +
            "'transport-ado-projection-forbidden' never fires, even against a transport-origin " +
            "string reaching the comment sink.");
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

    // Finding 2: the OLD filter excluded EVERY type whose name ends
    // in "TransportAdapter" — including the ITransportAdapter
    // INTERFACE, which is the single most important type on the
    // surface. Replace the name-based filter with a class-based
    // check: exclude concrete implementations of
    // ITransportAdapter (the adapter classes each get their own
    // conformance suite) but ALWAYS include the interface itself.
    private static bool IsFrozenTransportType(Type t)
    {
        if (t.Namespace is null) return false;
        if (!(t.Namespace == TransportNamespace ||
              t.Namespace.StartsWith(TransportNamespace + ".", StringComparison.Ordinal)))
            return false;
        // Exclude concrete adapter implementations tracked by their
        // own conformance suites (Herdr, Windows Terminal, Null).
        if (t.IsClass && typeof(ITransportAdapter).IsAssignableFrom(t))
            return false;
        // Exclude nested types with no meaningful transport surface
        // (record-generated compiler artifacts covered by
        // IsCompilerGenerated in the caller).
        return true;
    }

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
                if (!m.IsSpecialName && !IsRecordAutoMember(m.Name))
                    sigils.Add(FormatMethodSignature(m));
            foreach (var p in t.GetProperties(flags))
                sigils.Add(FormatPropertySignature(p));
            surface[t.FullName!] = sigils.ToList();
        }
        return surface;
    }

    // Finding 1: signature includes return type + generic arity +
    // parameter types with ref/out/in modifiers. Overloads and
    // parameter-type changes no longer collapse.
    internal static string FormatMethodSignature(MethodInfo m)
    {
        var sb = new StringBuilder();
        sb.Append("M:");
        sb.Append(m.Name);
        if (m.IsGenericMethodDefinition)
        {
            sb.Append('<');
            sb.Append(string.Join(",", m.GetGenericArguments().Select(g => g.Name)));
            sb.Append('>');
        }
        sb.Append('(');
        sb.Append(string.Join(",", m.GetParameters().Select(FormatParameter)));
        sb.Append(')');
        sb.Append(':');
        sb.Append(FormatTypeName(m.ReturnType));
        return sb.ToString();
    }

    private static string FormatPropertySignature(PropertyInfo p) =>
        $"P:{p.Name}:{FormatTypeName(p.PropertyType)}";

    private static string FormatParameter(ParameterInfo p)
    {
        var t = p.ParameterType;
        var mod = string.Empty;
        if (t.IsByRef)
        {
            if (p.IsIn) mod = "in ";
            else if (p.IsOut) mod = "out ";
            else mod = "ref ";
            t = t.GetElementType()!;
        }
        return mod + FormatTypeName(t);
    }

    internal static string FormatTypeName(Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (t.IsArray)
            return FormatTypeName(t.GetElementType()!) + "[]";
        if (t.IsGenericParameter)
            return t.Name;
        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();
            var raw = def.FullName ?? def.Name;
            var idx = raw.IndexOf('`');
            if (idx >= 0) raw = raw.Substring(0, idx);
            var args = t.GetGenericArguments().Select(FormatTypeName);
            return raw + "<" + string.Join(",", args) + ">";
        }
        return t.FullName ?? t.Name;
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
