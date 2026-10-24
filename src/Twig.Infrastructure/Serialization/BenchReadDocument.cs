using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Serialization;

/// <summary>Version 2 cache-only explicit Bench read. Counts describe the mirror, not the source.</summary>
internal sealed record BenchReadDocument(
    int Version,
    string RequestedBench,
    string BenchId,
    string BenchName,
    BenchReadOrigin Origin,
    string Observation,
    IReadOnlyList<BenchReadMember> Members,
    BenchReadCompleteness Completeness,
    JsonElement Configuration,
    JsonElement Roots);

internal sealed record BenchReadOrigin(
    string ConnectionRef, string Organization, string Project, string Team,
    string BindingId, long BindingRevision, string IdentityId,
    string WorktreeRoot, string WorktreeFingerprint, long AttachmentRevision,
    string SelectionSource, long SelectionRevision, string? StorageGeneration)
{
    /// <summary>Single construction site so every explicit Bench read reports one origin shape.</summary>
    internal static BenchReadOrigin From(ResolvedConnectionBinding origin) => new(origin.Binding.ConnectionRef,
        origin.Operation.Organization, origin.Operation.Project, origin.Operation.Team, origin.Binding.BindingId,
        origin.Binding.Revision, origin.Identity.IdentityId, origin.WorktreeRoot, origin.Operation.WorktreeFingerprint,
        origin.Operation.AttachmentRevision, origin.SelectionSource, origin.SelectionRevision, origin.StorageGeneration);
}

internal sealed record BenchReadMember(
    int Id, bool Loaded, bool Selected, bool Seed, bool Pending,
    string? Title, string? Type, string? State, int? ParentId);

internal sealed record BenchReadCompleteness(
    int KnownMemberCount, int LoadedMemberCount, int UnloadedKnownMemberCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SourceMemberCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? UnknownMemberCount, bool SourceComplete,
    bool ProjectionComplete, int RenderedNodeCount,
    IReadOnlyList<int> OmittedLoadedMemberIds,
    IReadOnlyList<int> MissingAncestorIds,
    IReadOnlyList<int> PrunedAncestorIds,
    IReadOnlyList<int> DepthOmittedIds,
    int DepthDown, int DepthUp, string? WorkingLevel,
    IReadOnlyList<string> Reasons);

internal sealed record BenchReadEnvelope(BenchReadDocument Browser);
internal sealed record BenchReadError(string Error);

/// <summary>Why the requested item belongs to the explicitly selected Bench; flags mirror <see cref="BenchReadMember"/>.</summary>
internal sealed record BenchDetailMembership(bool Selected, bool Seed, bool Pending);

/// <summary>
/// Honest single-item coverage. The item total is never claimed: a null source child count means unknown,
/// and a cached child count only describes the mirror.
/// </summary>
internal sealed record BenchDetailCompleteness(
    bool SourceComplete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? SourceChildCount,
    int CachedChildCount, bool? ParentCached, bool LinksVerified, IReadOnlyList<string> Reasons);
