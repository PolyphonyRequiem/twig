namespace Twig.Domain.Services.ChangeProposals;

/// <summary>The outcome of evaluating an authorization against a proposal.</summary>
/// <param name="Authorized">True iff the proposal may proceed to apply.</param>
/// <param name="RequiredMode">
/// The mode the session's steering demanded. Reported on both outcomes so a caller can record
/// what was required, not just that something was missing.
/// </param>
/// <param name="Refusal">The refusal reason; <c>null</c> iff <paramref name="Authorized"/>.</param>
public readonly record struct ProposalAuthorizationDecision(
    bool Authorized,
    ProposalAuthorizationMode RequiredMode,
    string? Refusal);

/// <summary>
/// The Change Proposal authorization gate (Spec #729 §Authorization, AB#743).
/// <para>
/// Pure evaluation: given the digest recomputed from the proposal file, the session's steering
/// mode, and the authorization the caller supplied, it decides whether apply may proceed. It
/// performs no I/O and holds no state, so every rule below is directly testable and cannot
/// behave differently on one surface than another.
/// </para>
/// <para>
/// <b>Every path fails closed.</b> An absent record, a record bound to a different digest, a
/// record whose mode is not the one the steering seam requires, and a record naming no
/// authorizer are all refusals. There is no permissive default and no "unknown means allow"
/// branch: an apply is a real mutation of someone's board, so the only safe answer to an
/// unanswerable question is no.
/// </para>
/// <para>
/// <b>What this gate deliberately does NOT check.</b> Per Spec #729 §Authorization, additional
/// AFK preflight gates — a refreshed read of the target, primary-scope matching, local-claim
/// ownership, rationale content — are out of scope for AB#743 and are not introduced here.
/// Adding one silently would change what "authorized" means for every existing caller.
/// </para>
/// </summary>
public static class ProposalAuthorizationGate
{
    /// <summary>
    /// The mode an authorization must carry for a session steered as <paramref name="steering"/>.
    /// Only a mode resolving affirmatively to <see cref="SessionSteeringMode.Afk"/> permits a
    /// model authorization; <see cref="SessionSteeringMode.Unresolved"/> is human-steered.
    /// </summary>
    public static ProposalAuthorizationMode RequiredMode(SessionSteeringMode steering) =>
        steering == SessionSteeringMode.Afk
            ? ProposalAuthorizationMode.Model
            : ProposalAuthorizationMode.Human;

    /// <summary>
    /// Evaluates <paramref name="authorization"/> against the proposal identified by
    /// <paramref name="proposalDigest"/> under the given <paramref name="steering"/> mode.
    /// </summary>
    /// <param name="authorization">
    /// The authorization the caller supplied, or <c>null</c> when none was. Null is a refusal,
    /// not a request to prompt: this gate never interacts, so a surface that wants to collect a
    /// sign-off must do so before calling apply.
    /// </param>
    /// <param name="proposalDigest">The digest recomputed from the proposal file at apply time.</param>
    /// <param name="steering">The session steering mode read from <see cref="ISessionSteeringModeProvider"/>.</param>
    public static ProposalAuthorizationDecision Evaluate(
        ProposalAuthorization? authorization,
        string proposalDigest,
        SessionSteeringMode steering)
    {
        var required = RequiredMode(steering);

        if (authorization is null)
        {
            return new ProposalAuthorizationDecision(
                false,
                required,
                required == ProposalAuthorizationMode.Model
                    ? "Refusing to apply: this session is AFK-steered and no model authorization record was supplied."
                    : "Refusing to apply: no human sign-off was supplied for this proposal.");
        }

        // Digest binding before anything else. A record bound elsewhere is not a weaker
        // authorization for THIS proposal — it is an authorization for a different one, and
        // reporting it as a mode or identity problem would misdescribe the failure.
        if (!string.Equals(authorization.Digest, proposalDigest, StringComparison.Ordinal))
        {
            return new ProposalAuthorizationDecision(
                false,
                required,
                $"Refusing to apply: authorization is bound to digest {authorization.Digest}, "
                + $"but this proposal's digest is {proposalDigest}.");
        }

        if (authorization.Mode != required)
        {
            return new ProposalAuthorizationDecision(
                false,
                required,
                $"Refusing to apply: this session requires a "
                + $"{ProposalAuthorization.ModeToWire(required)} authorization, but the supplied record is "
                + $"{ProposalAuthorization.ModeToWire(authorization.Mode)}.");
        }

        if (string.IsNullOrWhiteSpace(authorization.AuthorizerIdentity))
        {
            return new ProposalAuthorizationDecision(
                false,
                required,
                "Refusing to apply: the authorization names no authorizer identity.");
        }

        return new ProposalAuthorizationDecision(true, required, null);
    }
}
