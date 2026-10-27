namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// Legacy authorization mode retained for historic journal and outcome-receipt reads.
/// New proposal applies and retained-draft saves do not record this value.
/// </summary>
public enum ProposalAuthorizationMode
{
    /// <summary>Historic human authorization. Serialized as <c>human</c>.</summary>
    Human = 0,

    /// <summary>Historic model authorization. Serialized as <c>model</c>.</summary>
    Model = 1,
}

/// <summary>
/// Legacy authorization data bound to one Change Proposal digest.
/// <para>
/// New proposal applies and retained-draft saves do not create or require this record. The
/// fields remain available to read historic journal and outcome-receipt data.
/// </para>
/// </summary>
public sealed record ProposalAuthorization
{
    /// <summary>
    /// The proposal digest this historic authorization record was bound to.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>Historic authorization mode.</summary>
    public required ProposalAuthorizationMode Mode { get; init; }

    /// <summary>
    /// The identity recorded by a historic authorization. It is caller-supplied metadata,
    /// not proof of identity.
    /// </summary>
    public required string AuthorizerIdentity { get; init; }

    /// <summary>
    /// Historic caller-supplied rationale, when present.
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>When the historic authorization record was created.</summary>
    public required DateTimeOffset AuthorizedAt { get; init; }

    /// <summary>The historic wire spelling of <paramref name="mode"/>.</summary>
    public static string ModeToWire(ProposalAuthorizationMode mode) => mode switch
    {
        ProposalAuthorizationMode.Human => "human",
        ProposalAuthorizationMode.Model => "model",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown authorization mode."),
    };

    /// <summary>
    /// Parses a historic persisted <c>authorization_mode</c> value. Returns <c>null</c> for
    /// unknown values, including <c>null</c>.
    /// </summary>
    public static ProposalAuthorizationMode? ModeFromWire(string? wire) => wire switch
    {
        "human" => ProposalAuthorizationMode.Human,
        "model" => ProposalAuthorizationMode.Model,
        _ => null,
    };
}
