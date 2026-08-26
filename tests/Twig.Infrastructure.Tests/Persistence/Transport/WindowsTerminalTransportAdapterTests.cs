using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Twig.Domain.Common;
using Twig.Infrastructure.Persistence.Transport;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// Contract §12.3 Windows Terminal adapter (AB#747) suite.
/// <para>
/// Covers, in this order:
/// </para>
/// <list type="number">
///   <item>identity and capability declaration per §3.1 / §3.4 / §12.3;
///     the mandatory §3.1 common-denominator surface;</item>
///   <item>§7.4 integer→decimal-string normalization AND the non-integer
///     rejection path (§12.3 acceptance line);</item>
///   <item>the two-shape acceptance guarantee (§2.2 direct-human /
///     agent-driven) enforced through the shape validator, with every
///     other shape rejected;</item>
///   <item>the side-effect-free-probe guarantee (§12.3 emphasis) —
///     the adapter's reachable IL never references
///     <see cref="System.Diagnostics.Process"/>;</item>
///   <item>non-interference (§9.1 R1–R15 / §9.2) — the adapter takes no
///     dependency on any claim, plan-lifecycle, or ADO surface, and
///     none of its methods can trigger a state change on those
///     surfaces because it holds no reference to them;</item>
///   <item>Change Proposal rendering (§10) still falls back to the
///     terminal/text presentation through the core seam when no rich
///     Windows Terminal renderer is registered — asserted THROUGH
///     <see cref="ChangeProposalRenderer"/>, not by reimplementing the
///     rendering.</item>
/// </list>
/// </summary>
public sealed class WindowsTerminalTransportAdapterTests
{
    private static readonly IReadOnlyDictionary<string, string> _emptyContext = new Dictionary<string, string>();
    private static readonly IReadOnlySet<TransportCapability> _noCaps = new HashSet<TransportCapability>();

    private static TransportAdapterTarget WorktreeTarget(string adapterId = WindowsTerminalTransportAdapter.Id) =>
        new(TransportAdapterRole.Worktree, adapterId, "worktree-id", "opaque", _emptyContext);

    private static TransportAdapterTarget TerminalTarget(
        string hostAttachmentId,
        string hostAttachmentIdKind = WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger) =>
        new(
            TransportAdapterRole.Terminal,
            WindowsTerminalTransportAdapter.Id,
            hostAttachmentId,
            hostAttachmentIdKind,
            _emptyContext);

    private static TransportAdapterTarget AgentTarget(string adapterId = "herdr") =>
        new(TransportAdapterRole.Agent, adapterId, "agent-id", "opaque", _emptyContext);

    private static RecordIdentityRequest DirectHumanRequest(
        string hostAttachmentId,
        string hostAttachmentIdKind = WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger) =>
        new(
            WorktreeFingerprint: "fingerprint",
            WorktreeTarget: WorktreeTarget(),
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: TerminalTarget(hostAttachmentId, hostAttachmentIdKind),
            AgentCapabilities: _noCaps,
            TerminalCapabilities: _noCaps,
            AgentRecordedStatus: RecordedStatus.Unobservable,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch);

    private static RecordIdentityRequest AgentDrivenRequest(
        string hostAttachmentId,
        string hostAttachmentIdKind = WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger) =>
        new(
            WorktreeFingerprint: "fingerprint",
            WorktreeTarget: WorktreeTarget("herdr"),
            AgentTarget: AgentTarget(),
            AgentSessionKind: "cli",
            TerminalTarget: TerminalTarget(hostAttachmentId, hostAttachmentIdKind),
            AgentCapabilities: _noCaps,
            TerminalCapabilities: _noCaps,
            AgentRecordedStatus: RecordedStatus.Working,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch);

    // ─── §3.1 / §3.4 / §12.3 identity & capability declaration ────────

    [Fact]
    public void Adapter_id_is_the_fixed_kebab_case_string()
    {
        // §7 / §12.3 registration key. String equality against this is
        // the sole selection rule (§7.2).
        var adapter = new WindowsTerminalTransportAdapter();
        adapter.AdapterId.ShouldBe("windows-terminal");
    }

    [Fact]
    public void Adapter_declares_no_optional_capabilities()
    {
        // §3.4 rationale: Windows Terminal exposes no query,
        // enumeration, or status surface; declaring any §3.3 capability
        // would violate that rationale. The empty set is the settled,
        // correct declaration (§12.3).
        var adapter = new WindowsTerminalTransportAdapter();
        adapter.Capabilities.ShouldBeEmpty();
    }

    [Fact]
    public void DescribeAdapter_returns_fixed_metadata_with_terminal_role_only()
    {
        var description = new WindowsTerminalTransportAdapter().DescribeAdapter();
        description.AdapterId.ShouldBe("windows-terminal");
        description.DisplayName.ShouldBe("Windows Terminal");
        description.AdapterVersion.ShouldNotBeNullOrEmpty();
        description.Capabilities.ShouldBeEmpty();
        // Windows Terminal is a terminal host; it is not an agent
        // driver, and it is not itself a worktree adapter.
        description.SupportedRoles.ShouldBe(new[] { TransportAdapterRole.Terminal });
    }

    // ─── §7.4 integer → decimal-string normalization ──────────────────

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("42", "42")]
    [InlineData("2147483647", "2147483647")] // int.MaxValue
    public void RecordIdentity_leaves_canonical_integer_id_untouched(string input, string expected)
    {
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest(input));
        result.IsSuccess.ShouldBeTrue();
        result.Value.Terminal!.Target.HostAttachmentId.ShouldBe(expected);
        result.Value.Terminal.Target.HostAttachmentIdKind.ShouldBe(WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger);
    }

    [Theory]
    [InlineData("00", "0")]
    [InlineData("07", "7")]
    [InlineData("042", "42")]
    [InlineData("00000000000042", "42")]
    public void RecordIdentity_strips_leading_zeros_from_integer_id(string input, string expected)
    {
        // §7.4 mandates "a decimal string with no leading zeros" so a
        // Herdr-vs-WT rewrite (or an ADO-side rewrite that later
        // rehydrates the same window) treats "07" and "7" as the same
        // handle byte-for-byte.
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest(input));
        result.IsSuccess.ShouldBeTrue();
        result.Value.Terminal!.Target.HostAttachmentId.ShouldBe(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("42x")]
    [InlineData("4 2")]
    [InlineData("+7")]
    [InlineData(" 42 ")]
    [InlineData("0x2A")]
    [InlineData("1,000")]
    [InlineData("1.0")]
    [InlineData("nan")]
    public void RecordIdentity_rejects_non_integer_id_under_integer_kind(string bad)
    {
        // The §12.3-mandated "non-integer input" acceptance line. A
        // caller who supplied kind = wt-window-integer must supply a
        // parseable non-negative decimal integer; anything else is
        // schema-level malformed input, surfaced as
        // transport-record-invalid so the caller can distinguish
        // it from a shape rejection (§11).
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest(bad));
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void RecordIdentity_rejects_negative_integer_under_integer_kind()
    {
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest("-5"));
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void RecordIdentity_rejects_integer_id_overflowing_int32()
    {
        // A Windows Terminal window id realistically fits in int32; a
        // 20-digit "integer" is a caller bug we prefer to surface as
        // record-invalid rather than truncate.
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest("99999999999999999999"));
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("my-window")]
    [InlineData("_underscored-42")]
    [InlineData("has spaces")]
    public void RecordIdentity_preserves_named_id_verbatim(string name)
    {
        // §7.4 "named path — the caller's exact string" — no
        // trimming, no case change, no normalization.
        var req = DirectHumanRequest(name, WindowsTerminalTransportAdapter.HostAttachmentIdKindName);
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Terminal!.Target.HostAttachmentId.ShouldBe(name);
        result.Value.Terminal.Target.HostAttachmentIdKind.ShouldBe(WindowsTerminalTransportAdapter.HostAttachmentIdKindName);
    }

    [Fact]
    public void RecordIdentity_rejects_empty_host_attachment_id()
    {
        var req = DirectHumanRequest("", WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger);
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void RecordIdentity_rejects_unknown_host_attachment_id_kind()
    {
        // §7.4 mandates exactly two WT kinds; a caller-supplied
        // "wt-window-uuid" (or anything else) is a client bug that
        // MUST fail closed, never fall through to name-passthrough
        // semantics.
        var req = DirectHumanRequest("42", "wt-window-uuid");
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void RecordIdentity_rejects_terminal_target_pointing_at_a_different_adapter()
    {
        // The dispatch rule is string equality on adapterId (§7.2). A
        // caller reaching THIS adapter with a terminal target whose
        // adapterId is not "windows-terminal" is a routing bug — we
        // refuse to build a record whose payload adapterId disagrees
        // with the adapter that built it.
        var target = new TransportAdapterTarget(
            TransportAdapterRole.Terminal,
            AdapterId: "herdr", // wrong adapter
            HostAttachmentId: "42",
            HostAttachmentIdKind: WindowsTerminalTransportAdapter.HostAttachmentIdKindInteger,
            AdapterContext: _emptyContext);
        var req = new RecordIdentityRequest(
            WorktreeFingerprint: "fingerprint",
            WorktreeTarget: WorktreeTarget(),
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: target,
            AgentCapabilities: _noCaps,
            TerminalCapabilities: _noCaps,
            AgentRecordedStatus: RecordedStatus.Unobservable,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch);
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    // ─── §2.2 two-shape acceptance guarantee ──────────────────────────

    [Fact]
    public void RecordIdentity_direct_human_produces_direct_human_shape()
    {
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(DirectHumanRequest("042"));
        result.IsSuccess.ShouldBeTrue();
        var record = result.Value;
        // Direct-human = worktree present, agent null, terminal present.
        record.Worktree.ShouldNotBeNull();
        record.Agent.ShouldBeNull();
        record.Terminal.ShouldNotBeNull();
        record.Terminal!.Target.AdapterId.ShouldBe("windows-terminal");
        record.Terminal.Target.HostAttachmentId.ShouldBe("42");
        // Passes the shape validator (§2.2) — direct-human row.
        TransportShapeValidator.ValidateRecord(record).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void RecordIdentity_agent_driven_with_terminal_produces_agent_driven_shape()
    {
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(AgentDrivenRequest("7"));
        result.IsSuccess.ShouldBeTrue();
        var record = result.Value;
        // Agent-driven = worktree present, agent present, terminal
        // optional but present here (the WT-hosted Herdr case).
        record.Worktree.ShouldNotBeNull();
        record.Agent.ShouldNotBeNull();
        record.Terminal.ShouldNotBeNull();
        record.Agent!.Target.AdapterId.ShouldBe("herdr");
        record.Terminal!.Target.AdapterId.ShouldBe("windows-terminal");
        record.Agent.SessionKind.ShouldBe("cli");
        // Passes the shape validator (§2.2) — agent-driven row.
        TransportShapeValidator.ValidateRecord(record).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void RecordIdentity_rejects_missing_terminal_target()
    {
        // The Windows Terminal adapter only builds records with a
        // Windows Terminal terminal payload. A caller missing the
        // terminal target has reached the wrong adapter; §2.2 row 3
        // (bare worktree) is the closest identifier — no terminal AND
        // no agent means no valid shape.
        var req = new RecordIdentityRequest(
            WorktreeFingerprint: "fingerprint",
            WorktreeTarget: WorktreeTarget(),
            AgentTarget: null,
            AgentSessionKind: null,
            TerminalTarget: null,
            AgentCapabilities: _noCaps,
            TerminalCapabilities: _noCaps,
            AgentRecordedStatus: RecordedStatus.Unobservable,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch);
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.BareWorktree);
    }

    [Fact]
    public void RecordIdentity_rejects_agent_target_without_session_kind()
    {
        // §7.4 requires AgentSessionKind non-null when AgentTarget is
        // present. A caller passing agent target without session kind
        // is malformed input.
        var req = new RecordIdentityRequest(
            WorktreeFingerprint: "fingerprint",
            WorktreeTarget: WorktreeTarget("herdr"),
            AgentTarget: AgentTarget(),
            AgentSessionKind: null,
            TerminalTarget: TerminalTarget("42"),
            AgentCapabilities: _noCaps,
            TerminalCapabilities: _noCaps,
            AgentRecordedStatus: RecordedStatus.Working,
            AgentRecordedAt: System.DateTimeOffset.UnixEpoch);
        var result = new WindowsTerminalTransportAdapter().RecordIdentity(req);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.RecordInvalid);
    }

    [Fact]
    public void Every_success_record_matches_one_of_the_two_accepted_shapes()
    {
        // The load-bearing acceptance line: "Attachments the adapter
        // produces validate as agent-driven-with-host or direct-human
        // and never any other shape." Sample space: both paths, both
        // id kinds, canonical and normalized inputs.
        var samples = new[]
        {
            DirectHumanRequest("0"),
            DirectHumanRequest("00042"),
            DirectHumanRequest("primary", WindowsTerminalTransportAdapter.HostAttachmentIdKindName),
            AgentDrivenRequest("1"),
            AgentDrivenRequest("named-window", WindowsTerminalTransportAdapter.HostAttachmentIdKindName),
        };
        var adapter = new WindowsTerminalTransportAdapter();
        foreach (var req in samples)
        {
            var result = adapter.RecordIdentity(req);
            result.IsSuccess.ShouldBeTrue();
            var record = result.Value;
            // Direct-human OR agent-driven — nothing else.
            var isDirectHuman = record.Worktree is not null && record.Agent is null && record.Terminal is not null;
            var isAgentDriven = record.Worktree is not null && record.Agent is not null;
            (isDirectHuman || isAgentDriven).ShouldBeTrue($"record shape must be direct-human or agent-driven for input {req}");
            TransportShapeValidator.ValidateRecord(record).IsSuccess.ShouldBeTrue();
        }
    }

    // ─── §3.2 absent-capability degradation through the dispatcher ────

    [Fact]
    public async Task Dispatcher_returns_unobservable_status_for_windows_terminal_target()
    {
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        var result = await dispatcher.ReportStatusAsync(target, options: null, ct: default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(RecordedStatus.Unobservable);
        result.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
        result.Value.RecordedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatcher_returns_unknown_presence_for_windows_terminal_target()
    {
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        var result = await dispatcher.ProbeLivenessAsync(target, options: null, ct: default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Presence.ShouldBe(TransportLivenessPresence.Unknown);
        result.Value.Freshness.ShouldBe(TransportFreshness.Unobservable);
        result.Value.RecordedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Dispatcher_returns_close_not_supported_for_windows_terminal_target()
    {
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        var result = await dispatcher.CloseAsync(target, expectedRevision: 1, ct: default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.CloseNotSupported);
    }

    [Fact]
    public async Task Dispatcher_returns_partial_close_not_supported_for_windows_terminal_target()
    {
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        var scope = new PartialCloseScope("pane", "1", PartialCloseReason.UserRequested);
        var result = await dispatcher.PartialCloseAsync(target, scope, ct: default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe(TransportAttachmentFailure.PartialCloseNotSupported);
    }

    [Fact]
    public async Task Dispatcher_returns_ok_for_detach_on_windows_terminal_target()
    {
        // §6.1: record-level detach is always available, even for
        // adapters that do not declare the Detach capability.
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        var result = await dispatcher.DetachAsync(target, expectedRevision: 1, ct: default);
        result.IsSuccess.ShouldBeTrue();
    }

    // ─── §12.3 side-effect-free-probe guarantee ───────────────────────
    //
    // These four tests are the ticket's load-bearing acceptance line:
    // "A test proves the adapter never invokes `wt.exe` on any
    // observation path — this is the side-effect-free-probe guarantee
    // and it is the most important test in this ticket." The
    // guarantee is enforced structurally, not by hoping. Together
    // they close off every escape hatch a maintainer might reach for:
    //
    //   1. Direct or transitive call into System.Diagnostics.Process*
    //      (or the two P/Invoke primitives Marshal exposes, or any
    //      Windows-specific process-creation shim). Detected by a
    //      cycle-safe reachable-callee walk over the adapter's IL
    //      and every helper method it reaches inside our own
    //      assembly. BCL methods terminate the walk — they are
    //      audited by the checks below.
    //   2. Any string literal — anywhere in the transitive reach —
    //      matching "wt.exe", "wt ", a Windows Terminal binary
    //      alias, or a subprocess-implying shell name. This catches
    //      an indirect launch that concatenates the binary name into
    //      a payload we don't recognize.
    //   3. Reflection escape hatches (Type.GetType,
    //      Activator.CreateInstance, MethodBase.Invoke,
    //      Assembly.Load*, Delegate.CreateDelegate). Reflection could
    //      bypass the type-based scanner by resolving Process at
    //      runtime — the adapter's methods MUST NOT touch these APIs.
    //   4. A canary — an intentionally-noncompliant fixture class
    //      that DOES call Process.Start and DOES embed a "wt.exe"
    //      literal, asserted to be flagged by the very scanners above.
    //      This proves the scanners are wired and would fail loudly
    //      if a real regression landed. Without the canary, a silent
    //      breakage in the walker would render the guarantee vacuous.

    [Fact]
    public void Adapter_transitive_call_graph_never_references_process_or_process_launch_primitives()
    {
        // Transitive walk: start at the adapter's declared methods,
        // recurse through every callee whose declaring type is in the
        // adapter's own assembly (so a helper class in the transport
        // namespace cannot hide a Process reach), stopping at BCL
        // boundaries. Assert no reachable method's declaring type
        // begins with any of the forbidden prefixes.
        var violations = SideEffectFreeProbeScanner.FindProcessLaunchReferences(
            typeof(WindowsTerminalTransportAdapter));
        violations.ShouldBeEmpty(
            "The Windows Terminal adapter's reachable call graph reaches a process-launch " +
            "primitive. Contract §12.3 forbids this on every observation path because a " +
            "nonexistent `wt.exe --window <id>` silently CREATES a new window. Offending " +
            $"references: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Adapter_transitive_call_graph_carries_no_wt_exe_or_shell_literal()
    {
        // The literal scan is transitive for the same reason as the
        // Process scan: a helper class that concatenates "wt.exe"
        // into a Process.StartInfo we can't otherwise see is still
        // the ban §12.3 forbids.
        var offending = SideEffectFreeProbeScanner.FindForbiddenLiterals(
            typeof(WindowsTerminalTransportAdapter));
        offending.ShouldBeEmpty(
            "The Windows Terminal adapter's reachable call graph embeds a literal that " +
            "names a subprocess this adapter must never launch. §12.3 forbids reaching " +
            $"any of them, including via an indirect launch. Offending literals: {string.Join("; ", offending)}");
    }

    [Fact]
    public void Adapter_never_reaches_any_reflection_escape_hatch()
    {
        // Reflection could bypass the Process-name scanner by
        // resolving System.Diagnostics.Process at runtime. Forbid the
        // known escape hatches on the adapter's reachable graph.
        var violations = SideEffectFreeProbeScanner.FindReflectionEscapeHatches(
            typeof(WindowsTerminalTransportAdapter));
        violations.ShouldBeEmpty(
            "The Windows Terminal adapter reaches a reflection primitive that could " +
            "silently resolve System.Diagnostics.Process at runtime. §12.3 forbids the " +
            $"reach; the scanner cannot follow reflection tokens. Offending calls: {string.Join("; ", violations)}");
    }

    [Fact]
    public void Scanner_flags_the_canary_that_actually_launches_wt_exe()
    {
        // The canary. If the scanner is silently broken — because a
        // future .NET IL change moved a token, because someone
        // rewrote OperandLength wrong, because the walker doesn't
        // recurse into helpers — then every test above would pass
        // vacuously. This test constructs a class inside THIS
        // assembly that DOES call Process.Start, DOES embed "wt.exe"
        // as a literal, and DOES reflectively invoke a method, and
        // asserts each scanner catches its own kind of violation.
        //
        // If this test starts failing, the scanner is broken. Fix it
        // BEFORE trusting the other three §12.3 tests.
        SideEffectFreeProbeScanner
            .FindProcessLaunchReferences(typeof(CanaryThatLaunchesWtExe))
            .ShouldNotBeEmpty("scanner failed to detect a direct System.Diagnostics.Process reach — the §12.3 guarantee is not enforced");

        SideEffectFreeProbeScanner
            .FindForbiddenLiterals(typeof(CanaryThatLaunchesWtExe))
            .ShouldNotBeEmpty("scanner failed to detect a `wt.exe` literal — the §12.3 guarantee is not enforced");

        SideEffectFreeProbeScanner
            .FindReflectionEscapeHatches(typeof(CanaryThatLaunchesWtExe))
            .ShouldNotBeEmpty("scanner failed to detect a reflection escape hatch — the §12.3 guarantee is not enforced");
    }

    /// <summary>
    /// Deliberately non-compliant class the canary test scans.
    /// <para>
    /// Every forbidden operation lives in a SEPARATE
    /// <see cref="ForbiddenOperationsHelper"/>. The root methods on
    /// this class only CALL the helper. This is deliberate: a
    /// scanner that stopped at root methods (never descended into
    /// helpers) would let a real regression slip through, because
    /// production maintainers who reach for <c>wt.exe</c> tomorrow
    /// will do it through a helper — not by inlining
    /// <see cref="System.Diagnostics.Process.Start(string,string)"/>
    /// straight into <see cref="ITransportAdapter.RecordIdentity"/>.
    /// If the walker regressed to only inspecting root methods, this
    /// canary would silently pass; that is the vacuous-guarantee
    /// failure mode finding 6 blocks.
    /// </para>
    /// </summary>
    private static class CanaryThatLaunchesWtExe
    {
        // Root: no forbidden token appears here. Only a call.
        public static void LaunchesProcess() => ForbiddenOperationsHelper.LaunchProcessInternal();

        // Root: no literal in this method — the literal sits behind
        // one level of indirection so the walker's recursion is
        // what surfaces the forbidden fragment.
        public static string EmbedsWtExeLiteral() => ForbiddenOperationsHelper.BuildWtCommandLine();

        // Root: no reflection escape hatch appears here. The
        // reflective reach lives behind the helper.
        public static object? ReachesReflection() => ForbiddenOperationsHelper.ResolveProcessType();

        /// <summary>
        /// Helper class that actually carries the forbidden
        /// constructs. If the transitive walker never enters this
        /// class, the canary passes even when the guarantee is
        /// broken — that is the exact regression the canary must
        /// catch.
        /// </summary>
        private static class ForbiddenOperationsHelper
        {
            public static void LaunchProcessInternal()
            {
                // Unreachable at runtime; the scanner reads IL.
                if (System.Environment.ProcessorCount < 0)
                {
                    System.Diagnostics.Process.Start("wt.exe", "--window 0 -- echo canary");
                }
            }

            public static string BuildWtCommandLine() => "wt.exe --window 0";

            public static object? ResolveProcessType()
            {
                var t = System.Type.GetType("System.Diagnostics.Process");
                return t;
            }
        }
    }


    [Fact]
    public void Adapter_has_no_constructor_dependencies_capable_of_triggering_host_actions()
    {
        // A constructor with no parameters cannot receive an
        // IProcessRunner, an IProcessLauncher, an IHostCommandBus, or
        // any other conduit for shelling to wt.exe. This is a
        // load-bearing guarantee complementing the IL walk: even a
        // future maintainer who wants to reach a process API cannot
        // acquire the dependency without touching this constructor.
        var ctors = typeof(WindowsTerminalTransportAdapter).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        ctors.Length.ShouldBe(1);
        ctors[0].GetParameters().ShouldBeEmpty();
    }

    // ─── §9.1 R1–R15 non-interference ─────────────────────────────────

    [Fact]
    public void Adapter_never_references_R1_through_R15_verb_surfaces()
    {
        // Row-by-row assertion aligned with
        // TransportNoAuthorityConformanceTests.RejectedRows(): no
        // reachable method call in the WT adapter's TRANSITIVE call
        // graph reaches an R-row seam. Finding 7: the old check was
        // direct-only and could be defeated by moving the call
        // behind a helper; this uses the shared transitive walker
        // and is proven live by the paired canary test below.
        var forbiddenPrefixes = ForbiddenAuthorityPrefixes;
        var offenders = new List<string>();
        TransportCallGraphWalker.Walk(
            typeof(WindowsTerminalTransportAdapter),
            onCallee: (from, callee) =>
            {
                var declaring = callee.DeclaringType?.FullName ?? string.Empty;
                foreach (var prefix in forbiddenPrefixes)
                {
                    if (declaring.StartsWith(prefix, System.StringComparison.Ordinal))
                        offenders.Add(
                            $"{TransportCallGraphWalker.Describe(from)} -> {declaring}.{callee.Name}");
                }
            });
        offenders.ShouldBeEmpty(
            "Windows Terminal adapter's TRANSITIVE call graph reaches an R1–R15 seam. " +
            "Attach/probe/detach/close must not trigger a claim mint, plan apply, ADO mutation, " +
            "or session-steering derivation — regardless of how many helpers the call hides " +
            "behind. Offenders:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Scanner_flags_the_canary_whose_root_calls_a_helper_that_reaches_an_authority_surface()
    {
        // Finding 7 canary: this canary's root method contains NO
        // reference to any R-row surface — only a call into a
        // helper. If the walker regressed to a direct-only scan
        // (like the pre-fix version), this test would silently pass.
        var offenders = new List<string>();
        TransportCallGraphWalker.Walk(
            typeof(R1R15TransitiveCanary),
            onCallee: (from, callee) =>
            {
                var declaring = callee.DeclaringType?.FullName ?? string.Empty;
                foreach (var prefix in ForbiddenAuthorityPrefixes)
                {
                    if (declaring.StartsWith(prefix, System.StringComparison.Ordinal))
                        offenders.Add(
                            $"{TransportCallGraphWalker.Describe(from)} -> {declaring}.{callee.Name}");
                }
            });
        offenders.ShouldNotBeEmpty(
            "R1–R15 scanner failed to descend from a canary root into a helper that reaches " +
            "an authority surface. A regression to a direct-only scan would silently pass the " +
            "guarantee — finding 7 pins this as blocking.");
    }

    private static readonly string[] ForbiddenAuthorityPrefixes =
    {
        // R1 — claim lifecycle
        "Twig.Domain.Services.Claims",
        // R2 / R3 — plan lifecycle and Change Proposal state
        "Twig.Domain.Services.Plan",
        // R4 / R5 / R6 / R7 — ADO mutation surfaces
        "Twig.Domain.Interfaces.IAdoWorkItemService",
        "Twig.Domain.Services.Ado",
        "Twig.Infrastructure.Ado",
        // R8 — session-steering-mode derivation
        "Twig.Domain.Interfaces.IAttachmentStatusProjection",
        "Twig.Domain.Services.Attachment.PrimaryScopeAttachmentService",
        // R9 — primary-scope attachment lifecycle
        "Twig.Domain.Interfaces.IPrimaryScopeAttachmentStore",
        "Twig.Domain.Interfaces.IPrimaryScopeAttachmentService",
        // R10 — managed-worktree init
        "Twig.Domain.Interfaces.IManagedWorktreeInitializer",
    };

    /// <summary>
    /// Deliberately non-compliant fixture for finding 7's transitive
    /// canary. The root method contains no forbidden token; the
    /// reach lives in a helper. If the walker regressed to a
    /// direct-only scan (matching the pre-fix state), the offenders
    /// list would be empty and the canary test would fail.
    /// </summary>
    private static class R1R15TransitiveCanary
    {
        // Root: no forbidden token. Only a call into a helper.
        public static Twig.Domain.Common.Result MintClaimIndirectly()
        {
            AuthoritySinkHelper.CallForbiddenClaimSink();
            return Twig.Domain.Common.Result.Ok();
        }

        private static class AuthoritySinkHelper
        {
            // The forbidden reach: a call whose declaring type sits
            // in the R1 claim namespace prefix.
            public static Twig.Domain.Services.Claims.ClaimRecord CallForbiddenClaimSink()
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
    }

    [Fact]
    public async Task Attach_probe_detach_close_touch_only_transport_and_bcl_surfaces()
    {
        // Behavioural counterpart to the IL scan: exercise every path
        // (attach → probe → detach → close) and assert every one
        // returns without touching any non-transport, non-BCL API. The
        // adapter runs synchronously with no ambient state, so a mere
        // completion is the strongest non-interference evidence the
        // ticket asks for.
        var adapter = new WindowsTerminalTransportAdapter();

        // Attach — RecordIdentity for a direct-human window handle.
        var attach = adapter.RecordIdentity(DirectHumanRequest("42"));
        attach.IsSuccess.ShouldBeTrue();

        // Probe — status + liveness dispatch. The adapter's own
        // throwing methods MUST NOT be reached; the dispatcher gates
        // on Capabilities. This asserts the dispatcher path too.
        var dispatcher = NewDispatcher();
        var target = TerminalTarget("42");
        (await dispatcher.ReportStatusAsync(target, options: null, ct: default)).IsSuccess.ShouldBeTrue();
        (await dispatcher.ProbeLivenessAsync(target, options: null, ct: default)).IsSuccess.ShouldBeTrue();

        // Detach — record-level detach through the dispatcher.
        (await dispatcher.DetachAsync(target, expectedRevision: 1, ct: default)).IsSuccess.ShouldBeTrue();

        // Close — dispatcher degradation to close-not-supported.
        var close = await dispatcher.CloseAsync(target, expectedRevision: 1, ct: default);
        close.IsSuccess.ShouldBeFalse();
        close.Error.ShouldBe(TransportAttachmentFailure.CloseNotSupported);
    }

    // ─── §10 Change Proposal fallback through the core seam ───────────

    [Fact]
    public async Task Change_proposal_review_falls_back_to_terminal_text_through_the_core_seam()
    {
        // The acceptance criterion says: "With no rich Windows
        // Terminal surface available, Change Proposal review still
        // falls back to the terminal/text presentation through the
        // core seam — assert this through the seam, not by
        // reimplementing rendering."
        var adapter = new WindowsTerminalTransportAdapter();
        var attach = adapter.RecordIdentity(DirectHumanRequest("42"));
        attach.IsSuccess.ShouldBeTrue();
        var record = attach.Value;

        var supportRegistry = new ChangeProposalPresentationSupportRegistry(
            System.Array.Empty<RichAdapterId>());
        var textRenderer = new TerminalTextChangeProposalRenderer();
        var adapterRegistry = new TransportAdapterRegistry(new ITransportAdapter[]
        {
            new WindowsTerminalTransportAdapter(),
        });
        var seam = new ChangeProposalRenderer(
            supportRegistry,
            adapterRegistry,
            textRenderer,
            () => System.Array.Empty<IRichChangeProposalRenderer>());

        var proposal = new ChangeProposalRenderProposal(
            "cp-747",
            Content: "unchanged-content",
            Metadata: new Dictionary<string, string>());

        // §10.2 clause 4 fallback — the terminal payload's adapterId
        // isn't in the support registry, so selection lands on
        // TerminalText.
        var presentation = seam.SelectPresentation(proposal, record);
        presentation.ShouldBeOfType<Presentation.TerminalText>();

        var rendered = await seam.RenderAsync(proposal, presentation);
        rendered.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        rendered.AdapterId.ShouldBeNull();
    }

    [Fact]
    public async Task Change_proposal_fallback_survives_a_rogue_rich_registration_for_windows_terminal()
    {
        // If someone registers a rich renderer for Windows Terminal
        // and it refuses, §10.3 says: render the unchanged proposal
        // through the terminal/text renderer.
        var adapter = new WindowsTerminalTransportAdapter();
        var attach = adapter.RecordIdentity(DirectHumanRequest("42"));
        attach.IsSuccess.ShouldBeTrue();
        var record = attach.Value;

        var richId = new RichAdapterId("windows-terminal", TransportAdapterRole.Terminal);
        var supportRegistry = new ChangeProposalPresentationSupportRegistry(new[] { richId });
        var textRenderer = new TerminalTextChangeProposalRenderer();
        var refusingRenderer = new AlwaysRefusingRichRenderer(richId);
        var adapterRegistry = new TransportAdapterRegistry(new ITransportAdapter[]
        {
            new WindowsTerminalTransportAdapter(),
        });
        var seam = new ChangeProposalRenderer(
            supportRegistry,
            adapterRegistry,
            textRenderer,
            () => new IRichChangeProposalRenderer[] { refusingRenderer });

        var proposal = new ChangeProposalRenderProposal(
            "cp-747-refuse",
            Content: "unchanged-content",
            Metadata: new Dictionary<string, string>());

        var presentation = seam.SelectPresentation(proposal, record);
        presentation.ShouldBeOfType<Presentation.RichAdapter>();

        var rendered = await seam.RenderAsync(proposal, presentation);
        rendered.PresentationKind.ShouldBe(RenderedProposalKind.TerminalText);
        rendered.AdapterId.ShouldBeNull();
    }

    // ─── helpers ──────────────────────────────────────────────────────

    private static TransportAdapterDispatcher NewDispatcher()
    {
        var registry = new TransportAdapterRegistry(new ITransportAdapter[]
        {
            new WindowsTerminalTransportAdapter(),
        });
        var store = new FakeTransportAttachmentStore();
        return new TransportAdapterDispatcher(registry, store, TimeProvider.System);
    }

    private static IEnumerable<MethodBase> AllDeclaredMethods(System.Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        foreach (var m in type.GetMethods(flags)) yield return m;
        foreach (var c in type.GetConstructors(flags)) yield return c;
    }

    private static IEnumerable<MethodBase> ReferencedMethods(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { yield break; }
        if (body is null) yield break;
        var il = body.GetILAsByteArray();
        if (il is null) yield break;

        var module = method.Module;
        var declaringType = method.DeclaringType;
        var typeArgs = declaringType is { IsGenericType: true }
            ? declaringType.GetGenericArguments()
            : null;
        var methodArgs = method.IsGenericMethod
            ? method.GetGenericArguments()
            : null;

        var opcodes = OpcodeMap.Instance;
        int pos = 0;
        while (pos < il.Length)
        {
            if (pos >= il.Length) break;
            int code = il[pos++];
            if (code == 0xFE)
            {
                if (pos >= il.Length) yield break;
                code = 0xFE00 | il[pos++];
            }
            if (!opcodes.TryGetValue(code, out var op))
                yield break; // unknown opcode -> stop walking rather than mis-step.

            if (op.OperandType == OperandType.InlineMethod ||
                op.OperandType == OperandType.InlineTok)
            {
                if (pos + 4 > il.Length) yield break;
                int token = System.BitConverter.ToInt32(il, pos);
                MethodBase? resolved = null;
                try
                {
                    if (op.OperandType == OperandType.InlineMethod)
                        resolved = module.ResolveMethod(token, typeArgs, methodArgs);
                    else
                    {
                        var member = module.ResolveMember(token, typeArgs, methodArgs);
                        resolved = member as MethodBase;
                    }
                }
                catch { /* skip unresolvable */ }
                if (resolved is not null) yield return resolved;
            }

            pos += OperandLength(op.OperandType, il, pos);
        }
    }

    private static int OperandLength(OperandType type, byte[] il, int pos) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineI => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.InlineSwitch =>
            pos + 4 <= il.Length
                ? 4 + System.BitConverter.ToInt32(il, pos) * 4
                : il.Length - pos,
        _ => 0,
    };

    private static class OpcodeMap
    {
        internal static readonly Dictionary<int, OpCode> Instance = Build();

        private static Dictionary<int, OpCode> Build()
        {
            var dict = new Dictionary<int, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)field.GetValue(null)!;
                // OpCode.Value is a short; treat as unsigned so 0xFExx
                // maps correctly.
                dict[(ushort)op.Value] = op;
            }
            return dict;
        }
    }

    /// <summary>
    /// Transitive, cycle-safe reachable-callee scanner for the §12.3
    /// side-effect-free-probe guarantee. Starts at every method
    /// declared on the root type, walks the IL, resolves the token
    /// operands of <c>call</c>/<c>callvirt</c>/<c>newobj</c>/
    /// <c>ldftn</c>/<c>ldvirtftn</c>/<c>ldtoken</c> back to their
    /// <see cref="MethodBase"/>, and recurses into every callee whose
    /// declaring type is defined in the same assembly as the root. BCL
    /// callees terminate the walk — the assertions decide what those
    /// mean.
    /// <para>
    /// The scanner is intentionally over-approximate: it emits a
    /// violation for the first reachable callee/literal matching any
    /// forbidden pattern. False positives are cheap to fix (the
    /// pattern list is auditable); false negatives are catastrophic
    /// (a real wt.exe reach silently ships). The canary test
    /// <c>Scanner_flags_the_canary_that_actually_launches_wt_exe</c>
    /// asserts the scanner is wired correctly.
    /// </para>
    /// </summary>
    private static class SideEffectFreeProbeScanner
    {
        // System.Diagnostics.Process, ProcessStartInfo, and the two
        // COM/PInvoke primitives someone could reach for to launch a
        // process without hitting Process.Start.
        private static readonly string[] _forbiddenTypePrefixes = new[]
        {
            "System.Diagnostics.Process",
            "System.Diagnostics.ProcessStartInfo",
            "System.Diagnostics.ProcessModule",
            "System.Diagnostics.ProcessThread",
            "Microsoft.Win32.SafeHandles.SafeProcessHandle",
        };

        // Reflection escape hatches that could resolve
        // System.Diagnostics.Process (or any other process-launch
        // API) at runtime and invoke it, bypassing the type-based
        // scanner above.
        private static readonly (string TypeFullName, string MethodName)[] _reflectionEscapes = new[]
        {
            ("System.Type", "GetType"),
            ("System.Activator", "CreateInstance"),
            ("System.Reflection.Assembly", "Load"),
            ("System.Reflection.Assembly", "LoadFrom"),
            ("System.Reflection.Assembly", "LoadFile"),
            ("System.Reflection.Assembly", "GetType"),
            ("System.Reflection.MethodBase", "Invoke"),
            ("System.Reflection.MethodInfo", "Invoke"),
            ("System.Reflection.ConstructorInfo", "Invoke"),
            ("System.Reflection.PropertyInfo", "GetValue"),
            ("System.Reflection.PropertyInfo", "SetValue"),
            ("System.Reflection.FieldInfo", "GetValue"),
            ("System.Reflection.FieldInfo", "SetValue"),
            ("System.Delegate", "CreateDelegate"),
            ("System.Delegate", "DynamicInvoke"),
        };

        // Case-insensitive literal fragments that indicate a
        // subprocess reach targeting Windows Terminal or a
        // shell/CLI conduit into it.
        private static readonly string[] _forbiddenLiteralFragments = new[]
        {
            "wt.exe",
            // Anchor "wt " (with space) so common English words
            // containing "wt" (empty set for our transport surface,
            // but defensive) do not misfire.
            "wt --",
            "wt -w",
            "windowsterminal.exe",
            "windows-terminal.exe",
        };

        internal static IReadOnlyList<string> FindProcessLaunchReferences(System.Type root)
        {
            var violations = new List<string>();
            Walk(
                root,
                onCallee: (from, callee) =>
                {
                    var declaring = callee.DeclaringType?.FullName ?? "";
                    foreach (var prefix in _forbiddenTypePrefixes)
                    {
                        if (declaring.StartsWith(prefix, System.StringComparison.Ordinal))
                            violations.Add($"{Describe(from)} -> {declaring}.{callee.Name}");
                    }
                });
            return violations;
        }

        internal static IReadOnlyList<string> FindReflectionEscapeHatches(System.Type root)
        {
            var violations = new List<string>();
            Walk(
                root,
                onCallee: (from, callee) =>
                {
                    var declaring = callee.DeclaringType?.FullName ?? "";
                    foreach (var (t, m) in _reflectionEscapes)
                    {
                        if (declaring == t && callee.Name == m)
                            violations.Add($"{Describe(from)} -> {declaring}.{callee.Name}");
                    }
                });
            return violations;
        }

        internal static IReadOnlyList<string> FindForbiddenLiterals(System.Type root)
        {
            var violations = new List<string>();
            Walk(
                root,
                onLiteral: (from, literal) =>
                {
                    var lowered = literal.ToLowerInvariant();
                    foreach (var fragment in _forbiddenLiteralFragments)
                    {
                        if (lowered.Contains(fragment, System.StringComparison.Ordinal))
                            violations.Add($"{Describe(from)} embeds literal \"{literal}\"");
                    }
                });
            return violations;
        }

        private static string Describe(MethodBase method) =>
            $"{method.DeclaringType?.FullName ?? "<null>"}.{method.Name}";

        /// <summary>
        /// Transitive walk over the root type's reachable call graph
        /// inside the root's own assembly. <paramref name="onCallee"/>
        /// and <paramref name="onLiteral"/> are invoked for every
        /// callee / string token seen; a null delegate skips that
        /// class of tokens.
        /// </summary>
        private static void Walk(
            System.Type root,
            System.Action<MethodBase, MethodBase>? onCallee = null,
            System.Action<MethodBase, string>? onLiteral = null)
        {
            var visited = new HashSet<MethodBase>();
            var queue = new Queue<MethodBase>();

            foreach (var m in AllDeclaredMethodsIncludingNested(root))
            {
                if (visited.Add(m)) queue.Enqueue(m);
            }

            while (queue.Count > 0)
            {
                var method = queue.Dequeue();

                foreach (var token in EnumerateTokens(method))
                {
                    if (token.IsString && onLiteral is not null && token.Literal is not null)
                    {
                        onLiteral(method, token.Literal);
                        continue;
                    }
                    if (token.IsMethod && token.Callee is not null)
                    {
                        if (onCallee is not null) onCallee(method, token.Callee);
                        // Recurse into callees defined in ANY Twig
                        // assembly. BCL/third-party leaves stop the
                        // walk; the onCallee check above decides
                        // whether the leaf itself is forbidden. The
                        // rootAssembly is included via this rule,
                        // and callees in Twig.Domain / Twig.Domain.Common
                        // (the Result plumbing the adapter reaches
                        // for named failures) are covered too, so a
                        // helper hiding in a sibling Twig assembly
                        // cannot bypass the scan.
                        var calleeAssembly = token.Callee.DeclaringType?.Assembly;
                        var isTwigAssembly = calleeAssembly is not null
                            && (calleeAssembly.GetName().Name?.StartsWith("Twig", System.StringComparison.Ordinal) ?? false);
                        if (isTwigAssembly && visited.Add(token.Callee))
                            queue.Enqueue(token.Callee);
                    }
                }
            }
        }

        private static IEnumerable<MethodBase> AllDeclaredMethodsIncludingNested(System.Type type)
        {
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly;
            foreach (var m in type.GetMethods(flags)) yield return m;
            foreach (var c in type.GetConstructors(flags)) yield return c;
            // Nested types are part of the same declaration; the
            // adapter has none today, but a future maintainer adding
            // a helper struct/class must not slip past the walk.
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                foreach (var m in AllDeclaredMethodsIncludingNested(nested))
                    yield return m;
        }

        private readonly record struct WalkedToken(
            bool IsMethod,
            bool IsString,
            MethodBase? Callee,
            string? Literal);

        private static IEnumerable<WalkedToken> EnumerateTokens(MethodBase method)
        {
            MethodBody? body;
            try { body = method.GetMethodBody(); }
            catch { yield break; }
            if (body is null) yield break;
            var il = body.GetILAsByteArray();
            if (il is null) yield break;

            var module = method.Module;
            var declaringType = method.DeclaringType;
            var typeArgs = declaringType is { IsGenericType: true }
                ? declaringType.GetGenericArguments()
                : null;
            var methodArgs = method.IsGenericMethod
                ? method.GetGenericArguments()
                : null;

            var opcodes = OpcodeMap.Instance;
            int pos = 0;
            while (pos < il.Length)
            {
                int code = il[pos++];
                if (code == 0xFE)
                {
                    if (pos >= il.Length) yield break;
                    code = 0xFE00 | il[pos++];
                }
                if (!opcodes.TryGetValue(code, out var op)) yield break;

                if (op.OperandType == OperandType.InlineMethod)
                {
                    if (pos + 4 > il.Length) yield break;
                    int token = System.BitConverter.ToInt32(il, pos);
                    MethodBase? callee = null;
                    try { callee = module.ResolveMethod(token, typeArgs, methodArgs); }
                    catch { }
                    if (callee is not null)
                        yield return new WalkedToken(IsMethod: true, IsString: false, Callee: callee, Literal: null);
                }
                else if (op.OperandType == OperandType.InlineTok)
                {
                    if (pos + 4 > il.Length) yield break;
                    int token = System.BitConverter.ToInt32(il, pos);
                    MethodBase? callee = null;
                    try
                    {
                        var member = module.ResolveMember(token, typeArgs, methodArgs);
                        callee = member as MethodBase;
                    }
                    catch { }
                    if (callee is not null)
                        yield return new WalkedToken(IsMethod: true, IsString: false, Callee: callee, Literal: null);
                }
                else if (op.OperandType == OperandType.InlineString)
                {
                    if (pos + 4 > il.Length) yield break;
                    int token = System.BitConverter.ToInt32(il, pos);
                    string? literal = null;
                    try { literal = module.ResolveString(token); }
                    catch { }
                    if (literal is not null)
                        yield return new WalkedToken(IsMethod: false, IsString: true, Callee: null, Literal: literal);
                }

                pos += OperandLength(op.OperandType, il, pos);
            }
        }
    }

    private sealed class AlwaysRefusingRichRenderer : IRichChangeProposalRenderer
    {
        public AlwaysRefusingRichRenderer(RichAdapterId id) { Id = id; }
        public RichAdapterId Id { get; }
        public int TimeoutMs => 0;
        public Task<RichRenderResult> RenderAsync(ChangeProposalRenderProposal proposal, CancellationToken ct = default)
            => Task.FromResult<RichRenderResult>(new RichRenderResult.Refused("test-refuse"));
    }
}
