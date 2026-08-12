using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The single fetch seam the process description assembles from: everything the document
/// needs about a process, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This interface is the point at which byte-stability is testable.</b> The
/// whole-process path fetches types concurrently — parallelism is a RULED mitigation, not
/// an optimisation, because ~32 serial round-trips is ~20 s and a human notices. But
/// concurrency is also exactly what most plausibly breaks byte-stability, and a test that
/// tried to prove it by wall-clock timing would be flaky theatre.
/// </para>
/// <para>
/// So the seam is shaped so a test can drive COMPLETION ORDER explicitly: a fake
/// implementation can complete <see cref="GetTypeDetailAsync"/> for the types in any order
/// it likes, including exactly reversed, and the assembled document must be byte-identical
/// either way. That is the assertion the spec asks for, and it is only expressible because
/// per-type detail arrives through one awaitable call per type rather than through a
/// batch the provider orders internally.
/// </para>
/// <para>
/// 🔴 <b>Resolution is by process ID via the project, never by process name.</b> A live,
/// verified trap: the project named "Twig" does not run on the process named "Twig" — that
/// process owns zero projects. An implementation resolving by name silently describes the
/// wrong process, which destroys the one thing the document exists to be: a truth claim
/// about WHICH process you are looking at.
/// </para>
/// <para>
/// 🔴 <b>Nothing here caches.</b> A stale description is a WRONG description, and the whole
/// feature is a truth claim about a process at a moment in time. Implementations must
/// re-fetch on every call rather than trading the single property the artifact exists to
/// have for time saved on a command run rarely and deliberately.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md</c> — the seam section,
/// Solution S2, Implementation Decisions 3 and 11.
/// </para>
/// </remarks>
internal interface IProcessDescriptionSource
{
    /// <summary>
    /// Resolves which process to describe, by id, via the configured project.
    /// </summary>
    /// <returns>
    /// The process identity, or <c>null</c> when it cannot be resolved. <c>null</c> and a
    /// process with no types are different facts and must not be collapsed: "we could not
    /// ask" is not "this process is empty".
    /// </returns>
    Task<ProcessIdentity?> GetProcessIdentityAsync(CancellationToken ct = default);

    /// <summary>
    /// Lists the types belonging to the process.
    /// </summary>
    /// <remarks>
    /// 🔴 Returns REFERENCE names as identity. The description matches two processes by
    /// reference name because display names lie — one process was observed using reference
    /// names from an entirely differently-named process.
    /// <para>
    /// The order returned here is not trusted; the assembler sorts. This route also
    /// reports the process's OWN types, which is fewer than the project-scoped type list
    /// (that one includes system helper types the process does not report).
    /// </para>
    /// </remarks>
    /// <returns>
    /// The process's types, or <c>null</c> when the list cannot be fetched — never an empty
    /// list standing in for a failure. 🔴 On this route family a 404 arrives with a
    /// COUNT-SHAPED body, which is exactly the shape of a thin success, so laundering a
    /// failure into "no types" would produce a confident wrong answer.
    /// </returns>
    Task<IReadOnlyList<ProcessTypeSummary>?> GetTypesAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetches everything the document says about ONE type: its fields, its states, and its
    /// transitions.
    /// </summary>
    /// <remarks>
    /// One call per type, deliberately. This is the unit the whole-process path may run
    /// concurrently, and the unit a test drives completion order at.
    /// </remarks>
    /// <param name="typeReferenceName">
    /// The type's REFERENCE name. The process routes are keyed by reference name, not
    /// display name; sending a display name 404s against a real server.
    /// </param>
    /// <param name="inheritsFrom">
    /// 🔴 The parent type's reference name when this type is DERIVED from a system one, else
    /// <c>null</c>. Load-bearing, not decoration: a derived type is named
    /// <c>Niflheim.Epic</c> on the process routes but appears as
    /// <c>Microsoft.VSTS.WorkItemTypes.Epic</c> on the project-scoped route that is the only
    /// source of transitions. Without this an implementation looks up a name that route has
    /// never heard of and silently reports ZERO transitions — verified live, it hit exactly
    /// the three derived types in this process.
    /// </param>
    /// <returns>
    /// The type's detail, or <c>null</c> when it cannot be fetched. Same rule as above:
    /// "could not ask" is never rendered as "has nothing".
    /// </returns>
    Task<ProcessTypeDetail?> GetTypeDetailAsync(
        string typeReferenceName,
        string? inheritsFrom = null,
        CancellationToken ct = default);
}

/// <summary>Which process a description is about, and where it came from.</summary>
/// <param name="Organization">The organization URL.</param>
/// <param name="ProjectName">The project the process was resolved THROUGH.</param>
/// <param name="ProcessId">
/// The process template id — the identity the description is a truth claim about.
/// </param>
/// <param name="ProcessName">
/// The display name, for the reader only. 🔴 Never used to resolve: process names collide
/// across processes that own different projects.
/// </param>
internal sealed record ProcessIdentity(
    string Organization,
    string ProjectName,
    string ProcessId,
    string ProcessName);

/// <summary>A type as the process's own type list reports it.</summary>
/// <param name="ReferenceName">Stable identity. The key everything else is fetched by.</param>
/// <param name="Name">Display name, for the reader.</param>
/// <param name="Description">The server's description, or empty.</param>
/// <param name="Customization">
/// <c>custom</c> | <c>inherited</c> | <c>system</c>, verbatim. Authored-vs-inherited, which
/// is what lets a reader tell local customisation from what came with the parent process.
/// </param>
/// <param name="Inherits">Parent type reference name, or <c>null</c>.</param>
/// <param name="IsDisabled">Whether the process disabled the type.</param>
internal sealed record ProcessTypeSummary(
    string ReferenceName,
    string Name,
    string Description,
    string Customization,
    string? Inherits,
    bool IsDisabled);

/// <summary>Everything fetched for one type, before the assembler orders it.</summary>
/// <remarks>
/// Deliberately a plain carrier with no ordering promise of its own. The assembler is the
/// single place ordering is decided, so there is exactly one place to look when asking why
/// two documents differ.
/// </remarks>
/// <param name="Fields">The type's TYPE-SCOPED fields.</param>
/// <param name="States">The type's states.</param>
/// <param name="Transitions">The type's allowed transitions.</param>
/// <param name="Unfetched">
/// 🔴 Which parts could not be read, if any. A PARTIAL failure must be reported here rather
/// than passed off as an empty collection: on this route family a 404 is count-shaped and
/// looks exactly like thin success, so "the fields call failed" and "this type has no
/// fields" are indistinguishable downstream unless the failure is named. Collapsing them
/// launders a failed call into a confident wrong answer.
/// </param>
internal sealed record ProcessTypeDetail(
    IReadOnlyList<ProcessTypeField> Fields,
    IReadOnlyList<ProcessTypeState> States,
    IReadOnlyList<ProcessTypeTransition> Transitions,
    IReadOnlyList<string>? Unfetched = null);
