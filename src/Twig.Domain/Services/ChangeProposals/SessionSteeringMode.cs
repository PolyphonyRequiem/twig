namespace Twig.Domain.Services.ChangeProposals;

/// <summary>
/// How the current session is being steered, as consumed by the Change Proposal authorization
/// gate. This is a semantic property of the session per Spec #729 §Authorization.
/// </summary>
/// <remarks>
/// <see cref="Unresolved"/> is a first-class member rather than a null, because "we do not know
/// how this session is steered" is a real answer the gate must route on — and it routes it to
/// the human-steered path. An enum without it would force every provider to lie.
/// </remarks>
public enum SessionSteeringMode
{
    /// <summary>
    /// The session's steering mode could not be determined. Per Spec #729 the authorization path
    /// is then human-steered: only a mode that resolves affirmatively to <see cref="Afk"/> may
    /// take the model-authorization path.
    /// </summary>
    Unresolved = 0,

    /// <summary>A human is steering and must sign the proposal off.</summary>
    HumanSteered = 1,

    /// <summary>The session runs unattended; a model authorization record stands in for the human.</summary>
    Afk = 2,
}

/// <summary>
/// The minimum consumption interface for the session's steering mode — the seam the
/// authorization gate reads, and nothing more.
/// <para>
/// 🔴 <b>The mode's SOURCE is deliberately not defined here.</b> Spec #729 §Authorization defers
/// it to the session/authorization contract, and AB#743 names this interface rather than
/// inventing one. In particular an implementation MUST NOT derive the mode from a transport
/// attachment — the worktree it was launched in, an agent session id, the terminal host, an
/// environment variable a pane happened to inherit. A transport is how a session was delivered;
/// it is not evidence of how that session is being steered, and binding the two would let moving
/// a session between panes silently change who is allowed to authorize a mutation.
/// </para>
/// <para>
/// Until a real contract exists, the composition root binds
/// <see cref="UnresolvedSessionSteeringModeProvider"/>, so production resolves
/// <see cref="SessionSteeringMode.Unresolved"/> and every apply takes the human-steered path.
/// </para>
/// </summary>
public interface ISessionSteeringModeProvider
{
    /// <summary>Resolves the current session's steering mode.</summary>
    SessionSteeringMode Resolve();
}

/// <summary>
/// The production binding while the steering-mode source remains deferred: always
/// <see cref="SessionSteeringMode.Unresolved"/>, which the gate routes to the human-steered
/// path.
/// <para>
/// This is a complete implementation of the contract, not a placeholder. "No session/authorization
/// contract has supplied a mode" is exactly what it reports, and reporting it is what keeps the
/// system fail-closed. Returning <see cref="SessionSteeringMode.Afk"/> by guessing would be the
/// only unsafe answer available.
/// </para>
/// </summary>
public sealed class UnresolvedSessionSteeringModeProvider : ISessionSteeringModeProvider
{
    /// <inheritdoc />
    public SessionSteeringMode Resolve() => SessionSteeringMode.Unresolved;
}
