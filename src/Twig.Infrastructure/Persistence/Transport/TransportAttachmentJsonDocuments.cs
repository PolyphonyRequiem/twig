using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// On-disk shape for <c>.twig/transport.json</c> per contract §2.1.
/// Envelope + record + tombstone form. The persisted document is
/// hand-serialized rather than reflection-derived so:
/// <list type="bullet">
///   <item>capability strings validate against the §3.3 catalogue at
///     parse time (§2.2 row 6) — a §3.1 common-denominator name in a
///     persisted set fails with
///     <see cref="TransportAttachmentFailure.UnknownCapability"/>;</item>
///   <item>the six-value §4.1 recorded status validates at parse time
///     (§2.2 row 5);</item>
///   <item>every JSON node is registered in <see cref="TwigJsonContext"/>
///     via the DTO records here.</item>
/// </list>
/// </summary>
internal sealed record TransportAttachmentDocument(
    [property: JsonPropertyName("$schema")] string Schema,
    int Version,
    long Revision,
    string ConnectionRef,
    string RecordedAt,
    string State,
    TransportRecordDocument? Record)
{
    public const string CurrentSchema = "twig-transport-attachment/v1";
    public const int CurrentVersion = 1;
    public const string StateAttached = "attached";
    public const string StateDetached = "detached";
}

/// <summary>On-disk shape for the §2.1 record body. Each field is
/// independently nullable; the shape validator (§2.2) decides the
/// two accepted combinations.</summary>
internal sealed record TransportRecordDocument(
    TransportWorktreeDocument? Worktree,
    TransportAgentDocument? Agent,
    TransportTerminalDocument? Terminal);

internal sealed record TransportWorktreeDocument(
    string WorktreeFingerprint,
    TransportAdapterTargetDocument Target);

internal sealed record TransportAgentDocument(
    TransportAdapterTargetDocument Target,
    string SessionKind,
    string RecordedStatus,
    string RecordedAt,
    IReadOnlyList<string> Capabilities);

internal sealed record TransportTerminalDocument(
    TransportAdapterTargetDocument Target,
    IReadOnlyList<string> Capabilities);

internal sealed record TransportAdapterTargetDocument(
    string Role,
    string AdapterId,
    string HostAttachmentId,
    string HostAttachmentIdKind,
    IReadOnlyDictionary<string, string> AdapterContext);
