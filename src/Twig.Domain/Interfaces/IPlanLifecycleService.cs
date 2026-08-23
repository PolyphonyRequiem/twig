using Twig.Domain.Services.Plan;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The shared plan-lifecycle service. One implementation serves every surface (CLI, MCP,
/// TUI) so validation, preview, apply, status and seed-descriptor semantics cannot drift
/// between them.
/// <para>
/// Contract highlights:
/// </para>
/// <list type="bullet">
///   <item>Every method treats <c>file</c> as a filesystem path. The implementation resolves
///   it to an absolute path, refuses paths outside the current workspace root, and
///   validates the plan workspace (case-insensitive) against the active TwigConfiguration
///   before doing anything else.</item>
///   <item><see cref="PreviewAsync"/> parses and canonicalises the file, imports the journal
///   (idempotently), and returns a snapshot of every currently-staged pending change. Any
///   pending row present flips <c>CanApply</c> to <c>false</c>; the preview NEVER mutates
///   ADO.</item>
///   <item><see cref="ApplyAsync"/> refuses unless the recomputed digest of <c>file</c>
///   matches <c>confirmedDigest</c> exactly (case-sensitive lowercase-hex) and no pending
///   row exists. It confirms the journal, uses the internal execution engine to apply each
///   operation in order via the strict revision-bound ADO surface, and never re-issues an
///   operation the journal already saw. Failure of one operation stops later ones.</item>
///   <item><see cref="StatusAsync"/> reparses the file to recover the digest and returns the
///   journal snapshot, or <c>null</c> when no journal has ever been imported for that
///   digest.</item>
///   <item><see cref="DescribeSeedAsync"/> takes the negative display alias of a currently
///   staged seed, resolves it through <c>IStagedIdentityRegistry</c>, and returns its
///   durable <c>StagedIdentity</c>, canonical fingerprint (recomputed from current fields
///   plus all local seed links) and friendly metadata. A positive id, an unknown alias, an
///   alias whose seed has already been published, and any non-seed row all return
///   <c>null</c> — the descriptor is a plan-authoring convenience for STAGED seeds and
///   never speaks for a published item.</item>
/// </list>
/// </summary>
public interface IPlanLifecycleService
{
    /// <summary>Reads and validates a plan file, returning the parser's result.</summary>
    Task<PlanValidationResult> ValidateAsync(string file, CancellationToken ct = default);

    /// <summary>
    /// Validates the file, imports the journal (idempotent for a matching digest), and
    /// snapshots the current pending-change journal so the caller can render both. NEVER
    /// mutates ADO.
    /// </summary>
    Task<PlanPreviewResult> PreviewAsync(string file, CancellationToken ct = default);

    /// <summary>
    /// Applies the plan at <paramref name="file"/>. The implementation recomputes the
    /// file's canonical digest and refuses unless it equals <paramref name="confirmedDigest"/>
    /// exactly. Pending rows refuse. On success or per-operation failure the returned result
    /// carries the up-to-date per-operation journal.
    /// </summary>
    Task<PlanApplyResult> ApplyAsync(string file, string confirmedDigest, CancellationToken ct = default);

    /// <summary>
    /// Returns the journal snapshot for the digest of <paramref name="file"/>, or
    /// <c>null</c> when the file has never been previewed. Read-only.
    /// </summary>
    Task<PlanStatusResult?> StatusAsync(string file, CancellationToken ct = default);

    /// <summary>
    /// Returns a descriptor for the currently-staged seed at <paramref name="seedId"/> — a
    /// negative display alias that resolves through the durable staged-identity register.
    /// Returns <c>null</c> for a positive id, an unknown alias, an already-published seed, or
    /// any non-seed row. The fingerprint is recomputed from the current cache and equals
    /// exactly what the plan-apply pass will compare against.
    /// </summary>
    Task<PlanSeedDescriptor?> DescribeSeedAsync(int seedId, CancellationToken ct = default);
}
