namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// Who authorized a Change Proposal, in the closed vocabulary design record T2 (AB#741) §5.3
/// fixes for the journal's <c>authorization_mode</c> column.
/// <para>
/// <b>This is an audit fact, not a session property.</b> It is deliberately NOT the same
/// vocabulary as <c>Custom.WayfinderExecutionMode</c> (<c>HITL</c>/<c>AFK</c>), which describes
/// how a session is being steered. The two are kept distinct because conflating them makes an
/// audit trail unreconstructable: a session's steering mode can change, be resolved late, or be
/// unknown, while "a human signed this" and "a model signed this" are facts about one apply that
/// must never move afterwards.
/// </para>
/// </summary>
public enum ProposalAuthorizationMode
{
    /// <summary>A human sign-off. Serialized as <c>human</c>.</summary>
    Human = 0,

    /// <summary>A model authorization recorded on an AFK-steered session. Serialized as <c>model</c>.</summary>
    Model = 1,
}

/// <summary>
/// An authorization bound to one Change Proposal digest — the record the apply gate demands
/// before any operation runs, and the source of the journal's audit columns.
/// <para>
/// <b>Digest binding is the whole point.</b> <see cref="Digest"/> is the digest the authorizer
/// actually authorized, carried independently of the digest recomputed from the file at apply
/// time. Apply refuses unless the two are ordinally equal, so a sign-off cannot be replayed
/// against a proposal the authorizer never saw. Collapsing the two into one value would make
/// that failure unrepresentable rather than impossible.
/// </para>
/// <para>
/// <b>Never part of the digest.</b> Authorization is learned after parse, so per design record
/// T2 §3.4 it is journal data and never digest input. Hashing it would mean authorizing a
/// proposal changed its identity, which no apply could then match.
/// </para>
/// </summary>
public sealed record ProposalAuthorization
{
    /// <summary>
    /// The proposal digest this authorization is bound to — exactly 64 lowercase hex
    /// characters, compared ordinally against the digest recomputed from the file.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>Whether a human or a model authorized this proposal.</summary>
    public required ProposalAuthorizationMode Mode { get; init; }

    /// <summary>
    /// The signing human's identity, or the model identity for an AFK authorization. Must be
    /// non-blank: an audit row naming nobody records that something was authorized without
    /// recording who is answerable for it.
    /// <para>
    /// 🔴 <b>Non-blank is the only check, and it is weaker than it reads.</b> This is a name
    /// the constructing surface supplied, not a proven signer. Nothing here attests that the
    /// named party ever saw <see cref="Digest"/>. Spec #729 §Authorization makes authorizer
    /// separation an invariant and records the CLI apply path as known non-compliant with it,
    /// because that path fills this field from a caller-supplied <c>--authorize</c> string. An
    /// auditor reading this value learns who was <em>named</em>, which equals who
    /// <em>authorized</em> only on a surface that demonstrates separation.
    /// </para>
    /// </summary>
    public required string AuthorizerIdentity { get; init; }

    /// <summary>
    /// Why the authorizer approved this proposal; <c>null</c> when none was supplied. Optional
    /// per T2 §5.3 — it enriches the audit trail but is not what makes an apply legitimate.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>The moment the authorization was recorded.</summary>
    public required DateTimeOffset AuthorizedAt { get; init; }

    /// <summary>The wire spelling of <paramref name="mode"/> — the closed set <c>human|model</c>.</summary>
    public static string ModeToWire(ProposalAuthorizationMode mode) => mode switch
    {
        ProposalAuthorizationMode.Human => "human",
        ProposalAuthorizationMode.Model => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown authorization mode."),
    };

    /// <summary>
    /// Parses a persisted <c>authorization_mode</c> value. Returns <c>null</c> for anything
    /// outside the closed set, including <c>null</c> itself — a reader must be able to tell
    /// "predates authorization recording" from a mode it understands, and must never coerce an
    /// unrecognised value into one of the two it does.
    /// </summary>
    public static ProposalAuthorizationMode? ModeFromWire(string? wire) => wire switch
    {
        "human" => ProposalAuthorizationMode.Human,
        "model" => ProposalAuthorizationMode.Model,
        _ => null,
    };
}
