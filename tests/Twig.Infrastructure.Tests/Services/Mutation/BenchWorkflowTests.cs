using Shouldly;
using NSubstitute;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Mutation;

/// <summary>
/// ADO #148 — more than one Bench can exist, and the person can see what they have
/// (docs/specs/bench.spec.md §5).
/// <para>
/// Driven at the MUTATION-WORKFLOW seam, the one both the CLI and the agent surface route
/// through. Testing through the adapters instead would test the same logic twice and let the two
/// drift, which is the defect that made every agent-surface tool name its own target.
/// </para>
/// <para>
/// 🔴 The Bench repository here is REAL (in-memory SQLite), not a substitute. A substitute would
/// answer "does that name already exist?" from whatever the fixture was told to return, and the
/// name-collision tests would pass against an implementation that never wrote anything.
/// </para>
/// </summary>
public sealed class BenchWorkflowTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly IBenchRepository _benchRepo;

    public BenchWorkflowTests()
    {
        _benchRepo = new SqliteBenchRepository(_store);
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());
    }

    public void Dispose() => _store.Dispose();

    private BenchWorkflow CreateSut()
    {
        var selectors = new DefaultBenchSelectors(null);
        return new BenchWorkflow(_benchRepo, selectors, new CurrentBenchResolver(_benchRepo, selectors));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 1 — a named Bench can be created and appears in the listing
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_MakesABenchThatAppearsInTheListing()
    {
        var sut = CreateSut();

        // The discriminating precondition, asserted rather than assumed: the name really is not
        // there beforehand, so "it appears afterwards" cannot be satisfied by a listing that
        // always contained it.
        var before = await sut.ListAsync();
        before.Benches.ShouldNotContain(b => b.Name == "release blockers");

        var outcome = await sut.CreateAsync("release blockers");

        outcome.ShouldBeOfType<BenchOutcome.Created>()
            .Bench.Name.ShouldBe("release blockers");

        var after = await sut.ListAsync();
        after.Benches.ShouldContain(b => b.Name == "release blockers");
    }

    [Fact]
    public async Task Create_StoresTheBenchDurably_SoASeparateReadFindsIt()
    {
        var sut = CreateSut();
        await sut.CreateAsync("bugs I own");

        // Read through the repository directly, not through the same workflow instance, so an
        // implementation that only remembered the Bench in memory fails here.
        var stored = await _benchRepo.GetByNameAsync("bugs I own");
        stored.ShouldNotBeNull();
        stored!.IsDefault.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 2 — the listing shows which Bench is CURRENT
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_NamesTheCurrentBench_AndItIsOneOfTheListedBenches()
    {
        var sut = CreateSut();

        // 🔴 The name sorts BEFORE "default" deliberately. With only later-sorting names, an
        // implementation that reported "the first Bench in the listing" as current would agree
        // with a correct one on every assertion and the test would prove nothing.
        await sut.CreateAsync("aaa release blockers");
        await sut.CreateAsync("zzz bugs I own");

        var listing = await sut.ListAsync();

        // More than one Bench exists, so "the current one" is a real choice rather than the only
        // possible answer — a listing with one entry could not tell a correct marker from a
        // hard-coded one.
        listing.Benches.Count.ShouldBeGreaterThan(1);
        listing.Benches[0].Name.ShouldNotBe(Bench.DefaultName);
        listing.CurrentBenchName.ShouldBe(Bench.DefaultName);
        listing.Benches.ShouldContain(b => b.Name == listing.CurrentBenchName);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Acceptance 3 — the default Bench exists without the person creating it
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_OnAFreshStore_AlreadyContainsTheDefaultBench()
    {
        var sut = CreateSut();

        var listing = await sut.ListAsync();

        listing.Benches.ShouldContain(b => b.IsDefault && b.Name == Bench.DefaultName);
        listing.CurrentBenchName.ShouldBe(Bench.DefaultName);
    }

    [Fact]
    public async Task Create_AsTheVeryFirstCommand_StillLeavesTheDefaultBenchPresent()
    {
        var sut = CreateSut();

        // A person whose first-ever command is 'bench create' must not end up with a named Bench
        // and no default — the default is twig's to create and cannot go missing (spec §4).
        await sut.CreateAsync("release blockers");

        // 🔴 Read through the REPOSITORY, not through ListAsync. ListAsync creates the default
        // itself, so asserting through it would make this test pass against a create that never
        // touched the default — the fixture would have quietly supplied the thing under test.
        var stored = await _benchRepo.GetAllAsync();
        stored.ShouldContain(b => b.IsDefault);
    }

    [Fact]
    public async Task Create_DoesNotProduceASecondDefaultBench()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        await sut.CreateAsync("bugs I own");

        var listing = await sut.ListAsync();
        listing.Benches.Count(b => b.IsDefault).ShouldBe(1);
    }

    [Fact]
    public async Task ListingTheBenches_DoesNotDisturbTheDefaultBenchsSelectors()
    {
        // The default Bench is created with the sprint rule, and listing must not be a write path
        // that rebuilds or clears it — reading what exists is not an edit.
        var sut = CreateSut();
        var before = (await _benchRepo.GetOrCreateDefaultAsync(
            await new DefaultBenchSelectors(null).BuildAsync())).Selectors.ToList();
        before.ShouldNotBeEmpty();

        await sut.ListAsync();
        await sut.ListAsync();

        var after = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ToList();
        after.ShouldBe(before);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Names must identify a Bench a person can find again
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_WithANameThatIsTaken_CreatesNothingAndReportsTheExistingBench()
    {
        var sut = CreateSut();
        await sut.CreateAsync("release blockers");
        var countBefore = (await sut.ListAsync()).Benches.Count;

        var outcome = await sut.CreateAsync("release blockers");

        var exists = outcome.ShouldBeOfType<BenchOutcome.NameAlreadyExists>();
        exists.Existing.Name.ShouldBe("release blockers");
        (await sut.ListAsync()).Benches.Count.ShouldBe(countBefore);
    }

    [Fact]
    public async Task Create_DiffersOnlyByCase_IsTheSameName()
    {
        // Two Benches a listing cannot tell apart is the same defect as a name that does not
        // resolve: the person acts on the wrong one and nothing says so.
        var sut = CreateSut();
        await sut.CreateAsync("Release Blockers");

        var outcome = await sut.CreateAsync("release blockers");

        outcome.ShouldBeOfType<BenchOutcome.NameAlreadyExists>();
        (await sut.ListAsync()).Benches.Count(b => !b.IsDefault).ShouldBe(1);
    }

    [Fact]
    public async Task Create_CannotStealTheDefaultBenchsName()
    {
        var sut = CreateSut();

        var outcome = await sut.CreateAsync(Bench.DefaultName);

        outcome.ShouldBeOfType<BenchOutcome.NameAlreadyExists>();
        (await sut.ListAsync()).Benches.Count(b => b.IsDefault).ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithABlankName_IsRefusedAndCreatesNothing(string name)
    {
        var sut = CreateSut();
        var countBefore = (await sut.ListAsync()).Benches.Count;

        var outcome = await sut.CreateAsync(name);

        outcome.ShouldBeOfType<BenchOutcome.NameRejected>();
        (await sut.ListAsync()).Benches.Count.ShouldBe(countBefore);
    }

    [Fact]
    public async Task Create_TrimsSurroundingWhitespace_SoTheStoredNameIsTheOneTheyRead()
    {
        var sut = CreateSut();

        var outcome = await sut.CreateAsync("  release blockers  ");

        outcome.ShouldBeOfType<BenchOutcome.Created>().Bench.Name.ShouldBe("release blockers");
        (await sut.CreateAsync("release blockers")).ShouldBeOfType<BenchOutcome.NameAlreadyExists>();
    }

    // ═══════════════════════════════════════════════════════════════
    //  A new Bench is empty — creating one is not a way to copy an arrangement
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Create_MakesAnEmptyBench_NotACopyOfTheDefaultOne()
    {
        var sut = CreateSut();

        // Precondition: the default really does hold selectors, so "the new one holds none" is a
        // difference the test can see rather than two empty collections agreeing by accident.
        var listing = await sut.ListAsync();
        listing.Benches.Single(b => b.IsDefault).Selectors.ShouldNotBeEmpty();

        var created = (await sut.CreateAsync("release blockers")).ShouldBeOfType<BenchOutcome.Created>();

        created.Bench.Selectors.ShouldBeEmpty();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Ordering — a listing is something a person reads
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task List_OrdersByName_RegardlessOfCreationOrder()
    {
        var sut = CreateSut();
        await sut.CreateAsync("zebra");
        await sut.CreateAsync("alpha");

        var names = (await sut.ListAsync()).Benches.Select(b => b.Name).ToList();

        names.ShouldBe(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// A pin made on the current Bench is visible through the listing, which is the check a script
    /// makes before acting: the two verbs read the same Bench, not two copies of one.
    /// </summary>
    [Fact]
    public async Task List_ShowsSelectorsAddedByPinning_OnTheCurrentBench()
    {
        var pin = new PinWorkflow(
            _benchRepo, new DefaultBenchSelectors(null));
        await pin.PinAsync(4242, includeSubtree: false);

        var listing = await CreateSut().ListAsync();

        var current = listing.Benches.Single(b => b.Name == listing.CurrentBenchName);
        current.Selectors.ShouldContain(s => s.Kind == SelectorKind.Item && s.Payload == "4242");
    }
}
