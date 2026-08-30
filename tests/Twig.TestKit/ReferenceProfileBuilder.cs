using Twig.Domain.Common;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ReferenceProfile;
using Twig.Domain.ValueObjects;

namespace Twig.TestKit;

/// <summary>
/// Builds a <see cref="Twig.Domain.ValueObjects.ReferenceProfile"/> and the
/// seams over it, so tests can exercise profile-derived production gates
/// without reaching for the embedded release blob.
/// </summary>
/// <remarks>
/// The default shape mirrors the shipped profile's role→type bindings, because
/// a gate test that invented its own bindings would pass while production
/// failed. Individual bindings are overridable so a test can prove a gate reads
/// the PROFILE rather than a hardcoded type name — the property that
/// distinguishes a real seam from a literal.
/// </remarks>
public static class ReferenceProfileBuilder
{
    private static readonly StateEntry[] States =
    [
        new("To Do", StateCategory.Proposed, Color: null),
        new("Doing", StateCategory.InProgress, Color: null),
        new("Done", StateCategory.Completed, Color: null),
    ];

    /// <summary>
    /// Builds a valid profile. <paramref name="taskTypeName"/> overrides the
    /// sprint-tier (<see cref="Role.Task"/>) binding.
    /// </summary>
    public static Domain.ValueObjects.ReferenceProfile Build(
        string taskTypeName = "Task",
        string identity = "twig.reference-profile.hyperbright",
        string profileVersion = "1.0.0",
        string parentRef = "b8a3a935-7e91-48b8-a94c-606d37c3e9f2",
        string tailoringVersion = "basic:2026-08-24:1")
    {
        ReferenceProfileType Type(Role role, string typeName, string backlogRole, string behaviour) =>
            new(role, typeName, backlogRole, behaviour, States);

        return new Domain.ValueObjects.ReferenceProfile(
            identity,
            profileVersion,
            new ReferenceProfileBaseProcess(parentRef, tailoringVersion),
            new ReferenceProfileHierarchy(
                [Role.Initiative],
                [Role.Investigation, Role.Feature, Role.Bug],
                [Role.Task]),
            [
                Type(Role.Initiative, "Initiative", "portfolio", "Microsoft.VSTS.Basic.EpicBacklogBehavior"),
                Type(Role.Investigation, "Investigation", "requirement", "System.RequirementBacklogBehavior"),
                Type(Role.Feature, "Feature", "requirement", "System.RequirementBacklogBehavior"),
                Type(Role.Bug, "Bug", "requirement", "System.RequirementBacklogBehavior"),
                Type(Role.Task, taskTypeName, "task", "System.TaskBacklogBehavior"),
            ],
            [
                new(LinkKind.ParentChild, "decomposition",
                    "System.LinkTypes.Hierarchy-Forward", "System.LinkTypes.Hierarchy-Reverse"),
                new(LinkKind.PredecessorSuccessor, "blocking-sequencing",
                    "System.LinkTypes.Dependency-Forward", "System.LinkTypes.Dependency-Reverse"),
                new(LinkKind.Related, "informs", "System.LinkTypes.Related", null),
                new(LinkKind.Artifact, "evidence", null, null),
            ],
            new ReferenceProfilePrimaryScope(
                "ado-workitem",
                [Role.Initiative, Role.Investigation, Role.Feature, Role.Bug, Role.Task]),
            embeddedFingerprint: "0".PadLeft(64, '0'));
    }

    /// <summary>
    /// A provider whose <c>Load</c> and <c>ValidatePin</c> both succeed over
    /// <paramref name="profile"/> (defaulting to <see cref="Build"/>).
    /// </summary>
    public static IReferenceProfileProvider Provider(Domain.ValueObjects.ReferenceProfile? profile = null) =>
        new StubProvider(Result.Ok(profile ?? Build()), Result.Ok());

    /// <summary>
    /// A provider that fails with <paramref name="error"/>. Used to assert that
    /// gates fail CLOSED rather than permitting the operation when the profile
    /// or its pin is unusable.
    /// </summary>
    public static IReferenceProfileProvider FailingProvider(string error) =>
        new StubProvider(Result.Fail<Domain.ValueObjects.ReferenceProfile>(error), Result.Fail(error));

    /// <summary>
    /// A provider modelling a PINNED repository whose profile blob will not
    /// load. Both <c>Load</c> and <c>ValidatePin</c> report the load error,
    /// because that is what the real provider does: it resolves the pin's
    /// presence first, then propagates the load failure. Distinguished from
    /// <see cref="PinFailingProvider"/>, which models a repository whose pin is
    /// itself absent or wrong — the two must resolve in opposite directions.
    /// </summary>
    public static IReferenceProfileProvider LoadFailingButPinnedProvider(string error) =>
        new StubProvider(Result.Fail<Domain.ValueObjects.ReferenceProfile>(error), Result.Fail(error));

    /// <summary>A provider that loads but whose repository pin does not match.</summary>
    public static IReferenceProfileProvider PinFailingProvider(string error) =>
        new StubProvider(Result.Ok(Build()), Result.Fail(error));

    /// <summary>A <see cref="SprintEntryPolicy"/> over <see cref="Provider"/>.</summary>
    public static SprintEntryPolicy SprintPolicy(string taskTypeName = "Task") =>
        new(Provider(Build(taskTypeName)));

    /// <summary>
    /// A <see cref="SprintEntryPolicy"/> for a workspace that never pinned the
    /// reference profile — the shape of an ordinary Agile/Scrum repository, for
    /// which the T1 sprint-entry invariant is out of scope.
    /// </summary>
    public static SprintEntryPolicy UnpinnedSprintPolicy() =>
        new(PinFailingProvider(Domain.ValueObjects.ReferenceProfileErrors.TwigJsonProfileBlockMissing));

    private sealed class StubProvider(
        Result<Domain.ValueObjects.ReferenceProfile> load,
        Result pin) : IReferenceProfileProvider
    {
        public Result<Domain.ValueObjects.ReferenceProfile> Load() => load;

        public Result ValidatePin() => pin;

        public Result ValidateAgainstLiveProcess(
            IProcessConfigurationProvider liveProcess, string liveBaseProcessRef) =>
            throw new NotSupportedException("Not exercised by this stub.");

        public string ComputeLiveFingerprint(
            IProcessConfigurationProvider liveProcess, string liveBaseProcessRef) =>
            throw new NotSupportedException("Not exercised by this stub.");
    }
}
