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
using Twig.TestKit;
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

    private BenchWorkflow CreateSut(IIterationService? iterations = null)
    {
        var selectors = new DefaultBenchSelectors(iterations ?? IdentityStubs.NewBound());
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

    [Theory]
    [InlineData("unswitched")]
    [InlineData("default")]
    [InlineData("custom")]
    [InlineData("deleted-current")]
    public async Task List_WithStoredBenches_DoesNotRequireIdentityHttpAndPreservesStoredSelectors(string pointer)
    {
        var saved = new BenchQueryRule(null, "previous@example.test", [new("Project\\Team", true)],
            [BenchQueryRule.ParseSprint("@Current-1")]);
        var stored = await _benchRepo.GetOrCreateDefaultAsync([
            BenchSelector.ForCurrentSprint("Previous operator"), saved.ToSelector(),
            BenchSelector.ForItem(88), BenchSelector.ForSubtree(99)]);
        var custom = (await _benchRepo.CreateAsync("release blockers"))!;
        await _benchRepo.AddSelectorAsync(custom.Id, BenchSelector.ForCurrentSprintCanonical("authored@example.test"));
        if (pointer == "default")
            await _benchRepo.SetCurrentAsync(stored.Id);
        else if (pointer == "custom")
            await _benchRepo.SetCurrentAsync(custom.Id);
        else if (pointer == "deleted-current")
        {
            var deleted = (await _benchRepo.CreateAsync("deleted"))!;
            await _benchRepo.SetCurrentAsync(deleted.Id);
            await _benchRepo.DeleteAsync(deleted.Id);
        }
        var before = await _benchRepo.GetAllAsync();
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(string? DisplayName, string? UniqueName)>(
                new HttpRequestException("Identity profile HTTP is unavailable.")));

        var listing = await CreateSut(iterations).ListAsync();

        listing.CurrentBenchName.ShouldBe(pointer == "custom" ? custom.Name : stored.Name);
        foreach (var bench in before)
        {
            listing.Benches.Single(b => b.Id == bench.Id).Selectors.ShouldBe(bench.Selectors, ignoreOrder: true);
            (await _benchRepo.GetByNameAsync(bench.Name))!.Selectors.ShouldBe(bench.Selectors, ignoreOrder: true);
        }
        await iterations.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndSwitch_WithStoredDefault_DoNotRequireIdentityHttpOrRewriteSelectors(bool guardDefault)
    {
        var stored = await _benchRepo.GetOrCreateDefaultAsync([
            BenchSelector.ForCurrentSprintCanonical("previous@example.test"), BenchSelector.ForItem(88)]);
        var previous = (await _benchRepo.CreateAsync("previous arrangement"))!;
        await _benchRepo.AddSelectorAsync(previous.Id, BenchSelector.ForSubtree(99));
        await _benchRepo.SetCurrentAsync(previous.Id);
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(string? DisplayName, string? UniqueName)>(
                new HttpRequestException("Identity profile HTTP is unavailable.")));
        var sut = CreateSut(iterations);

        var created = (await sut.CreateAsync("  Release blockers  ")).ShouldBeOfType<BenchOutcome.Created>().Bench;
        created.Name.ShouldBe("Release blockers");
        created.Selectors.ShouldBeEmpty();
        (await _benchRepo.GetCurrentAsync())!.Id.ShouldBe(previous.Id);

        var selected = (await sut.SwitchAsync(created.Name,
            expectBench: created.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ShouldBeOfType<BenchOutcome.Switched>();
        selected.PreviousBenchName.ShouldBe(previous.Name);
        (await _benchRepo.GetCurrentAsync())!.Id.ShouldBe(created.Id);
        var back = (await sut.SwitchAsync(Bench.DefaultName, expectBench: guardDefault
            ? stored.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null))
            .ShouldBeOfType<BenchOutcome.Switched>();
        back.PreviousBenchName.ShouldBe(created.Name);
        (await _benchRepo.GetCurrentAsync())!.Id.ShouldBe(stored.Id);
        (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ShouldBe(stored.Selectors, ignoreOrder: true);
        (await _benchRepo.GetByNameAsync(previous.Name))!.Selectors.ShouldBe([BenchSelector.ForSubtree(99)]);
        await iterations.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
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
        listing.Benches.Single(b => b.IsDefault).Selectors.Single().QueryAssignedToUniqueName
            .ShouldBe(IdentityStubs.DefaultCanonicalPrincipal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task List_OnFirstUse_RefusesMissingCanonicalIdentityWithoutCreatingDefault(string? uniqueName)
    {
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)"Display name is not authority", uniqueName));

        await Should.ThrowAsync<InvalidOperationException>(() => CreateSut(iterations).ListAsync());

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task List_OnFirstUse_IdentityHttpFailureDoesNotCreateDefault()
    {
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(string? DisplayName, string? UniqueName)>(
                new HttpRequestException("Identity profile HTTP is unavailable.")));

        await Should.ThrowAsync<HttpRequestException>(() => CreateSut(iterations).ListAsync());

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("switch")]
    public async Task FirstUseLifecycle_RefusesMissingCanonicalIdentityWithoutCreatingOrSelectingBenches(string operation)
    {
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)"Display name is not authority", (string?)null));
        var sut = CreateSut(iterations);

        await Should.ThrowAsync<InvalidOperationException>(() => operation == "create"
            ? sut.CreateAsync("new arrangement") : sut.SwitchAsync(Bench.DefaultName));

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
        (await _benchRepo.GetCurrentAsync()).ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapturedResolution_OnFirstUse_RefusesStaleBenchBeforeIdentityDiscoveryOrCreation(bool storedOnly)
    {
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<(string? DisplayName, string? UniqueName)>(
                new HttpRequestException("Identity profile HTTP is unavailable.")));
        var resolver = new CurrentBenchResolver(_benchRepo, new DefaultBenchSelectors(iterations));

        await Should.ThrowAsync<InvalidOperationException>(() => storedOnly
            ? resolver.ResolveStoredAsync(expectBench: "previous-bench")
            : resolver.ResolveAsync(expectBench: "previous-bench"));

        (await _benchRepo.GetAllAsync()).ShouldBeEmpty();
        await iterations.DidNotReceive().GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>());
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
            await new DefaultBenchSelectors(IdentityStubs.NewBound()).BuildAsync())).Selectors.ToList();
        before.ShouldNotBeEmpty();

        await sut.ListAsync();
        await sut.ListAsync();

        var after = (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ToList();
        after.ShouldBe(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_DoesNotFreezeDefaultPinAndQueryEvaluationToThePreviouslyStoredActor(bool savedFilter)
    {
        var query = savedFilter
            ? new BenchQueryRule(null, "previous@example.test", [new("Project\\Team", false)],
                [BenchQueryRule.ParseSprint("Project\\Sprint 7")]).ToSelector()
            : BenchSelector.ForCurrentSprint("Same display");
        var stored = await _benchRepo.GetOrCreateDefaultAsync([query, BenchSelector.ForItem(88)]);
        var iterations = IdentityStubs.NewBound(displayName: "Same display", uniqueName: "first@example.test");
        var selectors = new DefaultBenchSelectors(iterations);
        var resolver = new CurrentBenchResolver(_benchRepo, selectors);
        var sut = new BenchWorkflow(_benchRepo, selectors, resolver);
        var repository = Substitute.For<IWorkItemRepository>();
        var sprint = IterationPath.Parse("Project\\Sprint 7").Value;
        repository.GetByIterationsAsync(Arg.Any<IReadOnlyList<IterationPath>>(), Arg.Any<CancellationToken>())
            .Returns([
                new WorkItemBuilder(1, "First operator's item").AssignedTo("Same display")
                    .AssignedToUniqueName("first@example.test").WithAreaPath("Project\\Team").WithIterationPath(sprint.Value).Build(),
                new WorkItemBuilder(2, "Second operator's item").AssignedTo("Same display")
                    .AssignedToUniqueName("second@example.test").WithAreaPath("Project\\Team").WithIterationPath(sprint.Value).Build()]);
        var calendar = Substitute.For<IIterationCalendar>();
        calendar.GetCurrentIterationsAsync(Arg.Any<CancellationToken>()).Returns(new[] { sprint });
        var evaluator = new BenchEvaluator(repository, calendar, Substitute.For<IPendingChangeStore>());

        (await sut.ListAsync()).Benches.Single(b => b.IsDefault).Selectors.ShouldBe(stored.Selectors, ignoreOrder: true);
        var pinned = (await new PinWorkflow(_benchRepo, selectors, resolver).PinAsync(77, includeSubtree: false))
            .ShouldBeOfType<PinOutcome.Pinned>();
        (await evaluator.EvaluateAsync(pinned.Bench)).AllIds.ShouldBe(new HashSet<int> { 1, 77, 88 }, ignoreOrder: true);

        iterations.WithBoundIdentity(displayName: "Same display", uniqueName: "second@example.test");
        var rawSelectors = new[] { query, BenchSelector.ForItem(77), BenchSelector.ForItem(88) };
        (await sut.ListAsync()).Benches.Single(b => b.IsDefault).Selectors.ShouldBe(rawSelectors, ignoreOrder: true);
        var (effective, raw) = await resolver.ResolveCapturedAsync();
        (await evaluator.EvaluateAsync(effective)).AllIds.ShouldBe(new HashSet<int> { 2, 77, 88 }, ignoreOrder: true);
        raw.Selectors.ShouldBe(rawSelectors, ignoreOrder: true);
        (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ShouldBe(rawSelectors, ignoreOrder: true);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("query")]
    public async Task StoredMetadataListing_DoesNotAuthorizeDefaultEvaluationWithoutCanonicalIdentity(string operation)
    {
        var stored = await _benchRepo.GetOrCreateDefaultAsync([
            BenchSelector.ForCurrentSprintCanonical("previous@example.test"), BenchSelector.ForItem(88)]);
        var iterations = Substitute.For<IIterationService>();
        iterations.GetAuthenticatedUserIdentityAsync(Arg.Any<CancellationToken>())
            .Returns(((string?)"Previous operator", (string?)null));
        var selectors = new DefaultBenchSelectors(iterations);
        var resolver = new CurrentBenchResolver(_benchRepo, selectors);
        (await new BenchWorkflow(_benchRepo, selectors, resolver).ListAsync()).CurrentBenchName.ShouldBe(stored.Name);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            if (operation == "pin")
                await new PinWorkflow(_benchRepo, selectors, resolver).PinAsync(77, includeSubtree: false);
            else
                await resolver.ResolveAsync();
        });

        (await _benchRepo.GetByNameAsync(Bench.DefaultName))!.Selectors.ShouldBe(stored.Selectors, ignoreOrder: true);
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
            _benchRepo, new DefaultBenchSelectors(IdentityStubs.NewBound()));
        await pin.PinAsync(4242, includeSubtree: false);

        var listing = await CreateSut().ListAsync();

        var current = listing.Benches.Single(b => b.Name == listing.CurrentBenchName);
        current.Selectors.ShouldContain(s => s.Kind == SelectorKind.Item && s.Payload == "4242");
    }
}
