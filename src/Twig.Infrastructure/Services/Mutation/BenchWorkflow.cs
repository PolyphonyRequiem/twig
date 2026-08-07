using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Workspace;

namespace Twig.Infrastructure.Services.Mutation;

/// <summary>
/// Creates named Benches and reports what exists (ADO #148, docs/specs/bench.spec.md §5).
/// <para>
/// This is the existing MUTATION-WORKFLOW seam, the same one <see cref="PinWorkflow"/> sits on:
/// one workflow per operation returning a result type, with both the CLI and the agent surface
/// routing through it. The adapters resolve the target and render the outcome and decide nothing
/// about what a Bench is, so the two surfaces cannot drift.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// 🔴 HUMAN SIMPLE, MACHINE STRICT (settled 2026-08-06). A person who names no Bench gets the
/// default; a script must name one every time, INCLUDING the default, and omitting it is a hard
/// error. That distinction is made by the CALLER declaring which it is — it is never inferred from
/// whether a terminal is attached. twig does not sniff for a tty: output shape and target
/// resolution are declared on the command, so a command means the same thing in a pipe as at a
/// prompt.
/// </para>
/// <para>
/// 🔴 Bench addressing is DELIBERATELY UNANSWERED (spec §8). This uses the simplest thing that
/// works — an explicit name — and is flagged as provisional so a later ruling that binds Bench
/// addressing to Context addressing does not have to undo a precedent quietly set here.
/// </para>
/// </remarks>
public sealed class BenchWorkflow(
    IBenchRepository benchRepository,
    DefaultBenchSelectors defaultSelectors,
    CurrentBenchResolver currentBench)
{
    /// <summary>
    /// Creates a Bench the person names. Never creates the default — that one is twig's to create
    /// (spec §4), and letting this verb produce it would give the person two ways to end up with a
    /// Bench whose selectors depend on which one they used.
    /// </summary>
    public async Task<BenchOutcome> CreateAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new BenchOutcome.NameRejected(
                name ?? string.Empty,
                "A Bench needs a name you will recognise later, such as 'release blockers'.");
        }

        var trimmed = name.Trim();

        // The default Bench must exist before anything is listed or created beside it, so a
        // person's first-ever command being 'bench create' cannot produce a world with a named
        // Bench and no default.
        await EnsureDefaultAsync(ct);

        var created = await benchRepository.CreateAsync(trimmed, ct);
        if (created is not null)
            return new BenchOutcome.Created(created);

        // Null means the name was taken — decided by the table's case-insensitive unique index,
        // not by a read here that a concurrent create could have raced.
        var existing = await benchRepository.GetByNameAsync(trimmed, ct);
        return existing is null
            ? new BenchOutcome.NameRejected(trimmed, "That Bench could not be created.")
            : new BenchOutcome.NameAlreadyExists(trimmed, existing);
    }

    /// <summary>
    /// Puts one arrangement down and picks another up. The Bench left behind is not touched: only
    /// the pointer moves, so switching back finds it exactly as it was — that is what makes this
    /// a switch rather than a rebuild.
    /// </summary>
    /// <remarks>
    /// 🔴 An unknown name CREATES NOTHING. Not a fallback, not a warning, and above all not a
    /// silently-created Bench. The lookup happens first and a miss returns
    /// <see cref="BenchOutcome.UnknownBench"/> before anything is written — including before the
    /// default Bench would be ensured, so a typo cannot even be the command that brings a Bench
    /// into existence as a side effect.
    /// </remarks>
    public async Task<BenchOutcome> SwitchAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new BenchOutcome.NameRejected(
                name ?? string.Empty, "Name the Bench you want to stand on, for example 'release blockers'.");
        }

        var trimmed = name.Trim();

        // The default cannot go missing (spec §4), so a switch TO it must succeed on a fresh
        // install where nothing has created it yet. It is ensured only on that branch: ensuring it
        // unconditionally would make an unknown name write a row, which is the side effect this
        // whole ticket forbids.
        if (string.Equals(trimmed, Bench.DefaultName, StringComparison.OrdinalIgnoreCase))
        {
            var previousName = (await currentBench.ResolveAsync(ct)).Name;
            var defaultBench = await benchRepository.GetOrCreateDefaultAsync(
                await defaultSelectors.BuildAsync(ct), ct);
            await benchRepository.SetCurrentAsync(defaultBench.Id, ct);
            return new BenchOutcome.Switched(defaultBench, previousName);
        }

        var target = await benchRepository.GetByNameAsync(trimmed, ct);
        if (target is null)
        {
            var known = (await benchRepository.GetAllAsync(ct)).Select(b => b.Name).ToList();
            return new BenchOutcome.UnknownBench(trimmed, known);
        }

        var previous = (await currentBench.ResolveAsync(ct)).Name;
        await benchRepository.SetCurrentAsync(target.Id, ct);
        return new BenchOutcome.Switched(target, previous);
    }

    /// <summary>
    /// Every Bench that exists, with the current one named. Creates the default first so the
    /// listing is never empty: the default exists without the person creating it (spec §4), and a
    /// listing that showed nothing until somebody pinned something would say something false.
    /// </summary>
    public async Task<BenchListing> ListAsync(CancellationToken ct = default)
    {
        var current = await currentBench.ResolveAsync(ct);
        var all = await benchRepository.GetAllAsync(ct);
        return new BenchListing(all, current.Name);
    }

    /// <summary>
    /// The default Bench, created on first use. Only <see cref="CreateAsync"/> calls this now:
    /// which Bench is CURRENT is <see cref="CurrentBenchResolver"/>'s single answer, shared with
    /// the view and the pin workflow, so no surface can be left reading the default after a
    /// switch.
    /// </summary>
    private async Task<Bench> EnsureDefaultAsync(CancellationToken ct)
        => await benchRepository.GetOrCreateDefaultAsync(await defaultSelectors.BuildAsync(ct), ct);
}
