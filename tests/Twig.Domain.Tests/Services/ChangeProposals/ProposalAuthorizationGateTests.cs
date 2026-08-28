using Shouldly;
using Twig.Domain.Services.ChangeProposals;
using Xunit;

namespace Twig.Domain.Tests.Services.ChangeProposals;

/// <summary>
/// The Change Proposal authorization gate (Spec #729 §Authorization, AB#743).
/// <para>
/// Each test names the bug it defends against, because a gate is only worth having if the
/// specific way it can be defeated is written down.
/// </para>
/// </summary>
public sealed class ProposalAuthorizationGateTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static ProposalAuthorization Record(
        string digest = Digest,
        ProposalAuthorizationMode mode = ProposalAuthorizationMode.Human,
        string identity = "Daniel Green",
        string? rationale = null) => new()
        {
            Digest = digest,
            Mode = mode,
            AuthorizerIdentity = identity,
            Rationale = rationale,
            AuthorizedAt = DateTimeOffset.UnixEpoch,
        };

    // Defends against: an apply that proceeds because nobody checked whether anyone signed
    // it off — the "no record means no objection" failure.
    [Theory]
    [InlineData(SessionSteeringMode.HumanSteered)]
    [InlineData(SessionSteeringMode.Afk)]
    [InlineData(SessionSteeringMode.Unresolved)]
    public void MissingAuthorization_FailsClosed_InEverySteeringMode(SessionSteeringMode steering)
    {
        var decision = ProposalAuthorizationGate.Evaluate(null, Digest, steering);

        decision.Authorized.ShouldBeFalse();
        decision.Refusal.ShouldNotBeNullOrWhiteSpace();
    }

    // Defends against: replaying yesterday's sign-off against today's proposal. The digest is
    // what an authorization means; without this check the record authorizes anything.
    [Fact]
    public void HumanSignOffBoundToADifferentDigest_FailsClosed()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(digest: OtherDigest), Digest, SessionSteeringMode.HumanSteered);

        decision.Authorized.ShouldBeFalse();
        decision.Refusal!.ShouldContain(OtherDigest);
        decision.Refusal!.ShouldContain(Digest);
    }

    // Same rule on the AFK path: an agent replaying a model authorization from an earlier
    // proposal must be refused exactly as a human replaying a sign-off would be.
    [Fact]
    public void ModelAuthorizationBoundToADifferentDigest_FailsClosed()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(digest: OtherDigest, mode: ProposalAuthorizationMode.Model),
            Digest,
            SessionSteeringMode.Afk);

        decision.Authorized.ShouldBeFalse();
    }

    // Defends against: a model authorizing its own apply in a session a human is steering.
    [Fact]
    public void ModelRecordInAHumanSteeredSession_FailsClosed()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(mode: ProposalAuthorizationMode.Model), Digest, SessionSteeringMode.HumanSteered);

        decision.Authorized.ShouldBeFalse();
        decision.Refusal!.ShouldContain("human");
        decision.Refusal!.ShouldContain("model");
    }

    // Defends against: an AFK run recording a human sign-off nobody actually gave, which would
    // make the audit trail claim a person approved an unattended apply.
    [Fact]
    public void HumanRecordInAnAfkSession_FailsClosed()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(mode: ProposalAuthorizationMode.Human), Digest, SessionSteeringMode.Afk);

        decision.Authorized.ShouldBeFalse();
    }

    // Defends against: an audit row recording that something was authorized without recording
    // who is answerable for it.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankAuthorizerIdentity_FailsClosed(string identity)
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(identity: identity), Digest, SessionSteeringMode.HumanSteered);

        decision.Authorized.ShouldBeFalse();
        decision.Refusal!.ShouldContain("authorizer identity");
    }

    // Spec #729: "When the session steering mode cannot be resolved as AFK, the authorization
    // path is human-steered." Defends against an unresolved mode being read as permission to
    // take the unattended path.
    [Fact]
    public void UnresolvedSteering_RequiresAHumanSignOff()
    {
        ProposalAuthorizationGate.RequiredMode(SessionSteeringMode.Unresolved)
            .ShouldBe(ProposalAuthorizationMode.Human);

        ProposalAuthorizationGate.Evaluate(
            Record(mode: ProposalAuthorizationMode.Model), Digest, SessionSteeringMode.Unresolved)
            .Authorized.ShouldBeFalse();

        ProposalAuthorizationGate.Evaluate(
            Record(mode: ProposalAuthorizationMode.Human), Digest, SessionSteeringMode.Unresolved)
            .Authorized.ShouldBeTrue();
    }

    [Fact]
    public void MatchingHumanSignOff_Authorizes()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(rationale: "Reviewed the operations."), Digest, SessionSteeringMode.HumanSteered);

        decision.Authorized.ShouldBeTrue();
        decision.Refusal.ShouldBeNull();
        decision.RequiredMode.ShouldBe(ProposalAuthorizationMode.Human);
    }

    [Fact]
    public void MatchingModelAuthorizationInAfk_Authorizes()
    {
        var decision = ProposalAuthorizationGate.Evaluate(
            Record(mode: ProposalAuthorizationMode.Model, identity: "twig-agent"),
            Digest,
            SessionSteeringMode.Afk);

        decision.Authorized.ShouldBeTrue();
        decision.RequiredMode.ShouldBe(ProposalAuthorizationMode.Model);
    }

    // 🔴 Spec #729 forbids sourcing the steering mode from a transport attachment. This asserts
    // the property that makes that enforceable: the decision is a function of the authorization,
    // the digest, and the steering mode ONLY. Ambient transport identity — the pane, worktree,
    // agent session, or work item a run happens to be attached to — cannot move it.
    //
    // Defends against: a future provider quietly reading one of these variables, which would
    // let moving a session between panes change who may authorize a mutation from it.
    [Theory]
    [InlineData(SessionSteeringMode.HumanSteered)]
    [InlineData(SessionSteeringMode.Afk)]
    public void Decision_IsIndependentOfTransportIdentity(SessionSteeringMode steering)
    {
        var record = Record(mode: ProposalAuthorizationGate.RequiredMode(steering));
        var baseline = ProposalAuthorizationGate.Evaluate(record, Digest, steering);

        string[] transportVariables = ["HERDR_ENV", "HERDR_TAB_ID", "WORK_ITEM", "BATON", "TERM"];
        var saved = transportVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var variable in transportVariables)
                Environment.SetEnvironmentVariable(variable, $"transport-{Guid.NewGuid():N}");

            // The production provider is part of the property under test: it must not start
            // answering differently because a transport marker appeared in the environment.
            new UnresolvedSessionSteeringModeProvider().Resolve().ShouldBe(SessionSteeringMode.Unresolved);

            var underTransport = ProposalAuthorizationGate.Evaluate(record, Digest, steering);
            underTransport.ShouldBe(baseline);
        }
        finally
        {
            foreach (var (variable, value) in saved)
                Environment.SetEnvironmentVariable(variable, value);
        }
    }

    // The wire vocabulary is a closed set of two, and it is not the HITL/AFK session vocabulary.
    // Defends against a reader coercing an unrecognised or absent value into one of the two it
    // understands — which would turn "predates authorization recording" into a false claim that
    // a specific party authorized the apply.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("HITL")]
    [InlineData("AFK")]
    [InlineData("Human")]
    public void ModeFromWire_RefusesAnythingOutsideTheClosedSet(string? wire)
        => ProposalAuthorization.ModeFromWire(wire).ShouldBeNull();

    [Fact]
    public void ModeWireSpellings_RoundTrip()
    {
        ProposalAuthorization.ModeToWire(ProposalAuthorizationMode.Human).ShouldBe("human");
        ProposalAuthorization.ModeToWire(ProposalAuthorizationMode.Model).ShouldBe("model");
        ProposalAuthorization.ModeFromWire("human").ShouldBe(ProposalAuthorizationMode.Human);
        ProposalAuthorization.ModeFromWire("model").ShouldBe(ProposalAuthorizationMode.Model);
    }
}
