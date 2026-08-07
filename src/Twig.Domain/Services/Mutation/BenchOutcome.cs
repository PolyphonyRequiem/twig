using Twig.Domain.Aggregates;

namespace Twig.Domain.Services.Mutation;

/// <summary>
/// Result of creating a Bench, produced by <c>BenchWorkflow</c> (ADO #148,
/// docs/specs/bench.spec.md §5).
/// </summary>
/// <remarks>
/// Both the CLI and the agent surface render this outcome; neither decides what creating a Bench
/// MEANS. A refused name is a VARIANT rather than an exception because it is an ordinary thing a
/// person does — asking for a name that is already taken — and the adapter has to say which name
/// and why, which an exception message would leave it to reconstruct from a string.
/// </remarks>
public abstract record BenchOutcome
{
    private BenchOutcome() { }

    /// <summary>The Bench now exists and will appear in the listing.</summary>
    /// <param name="Bench">The Bench that was created, with the name as stored.</param>
    public sealed record Created(Bench Bench) : BenchOutcome;

    /// <summary>
    /// Nothing was created: a Bench with that name already exists. Names are matched
    /// case-insensitively, so <c>Release</c> and <c>release</c> are the same Bench — the person
    /// cannot end up with two arrangements they cannot tell apart in a listing.
    /// </summary>
    /// <param name="RequestedName">The name the person asked for, as they typed it.</param>
    /// <param name="Existing">The Bench that already holds the name, with its stored spelling.</param>
    public sealed record NameAlreadyExists(string RequestedName, Bench Existing) : BenchOutcome;

    /// <summary>
    /// Nothing was created: the name cannot identify a Bench later. A person recognises an
    /// arrangement by its name, so a name that is blank is refused rather than stored.
    /// </summary>
    /// <param name="RequestedName">The name the person asked for, as they typed it.</param>
    /// <param name="Reason">What is wrong with it, phrased for the person to act on.</param>
    public sealed record NameRejected(string RequestedName, string Reason) : BenchOutcome;
}
