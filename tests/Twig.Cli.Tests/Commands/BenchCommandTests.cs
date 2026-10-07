using System.Globalization;
using Twig.Infrastructure.Auth;
using System.Reflection;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services.Mutation;
using Xunit;
using Twig.Cli.Tests.TestSupport;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// ADO #148 — the CLI adapter over <see cref="BenchWorkflow"/> (docs/specs/bench.spec.md §5).
/// <para>
/// What a Bench IS is tested once, at the workflow seam. These tests cover only what the adapter
/// decides: the exit code, and the two output shapes — the one a person reads and the one a script
/// parses.
/// </para>
/// <para>
/// 🔴 The format is DECLARED by the caller, never sniffed from whether a tty is attached, so the
/// machine-readable listing is asserted by ASKING for it rather than by redirecting output.
/// </para>
/// </summary>
public sealed class BenchCommandTests : IDisposable
{
    private readonly SqliteCacheStore _benchStore = new("Data Source=:memory:");
    private readonly ITrackingRepository _trackingRepo = Substitute.For<ITrackingRepository>();
    private readonly OutputFormatterFactory _formatterFactory = new(new HumanOutputFormatter());

    public void Dispose() => _benchStore.Dispose();

    private BenchCommand CreateCommand(IAuthenticationProvider? authentication = null,
        Func<CancellationToken, Task<ResolvedConnectionBinding>>? resolveBinding = null)
    {
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var repo = new SqliteBenchRepository(_benchStore);
        var selectors = new DefaultBenchSelectors(IdentityStubs.NewBound());
        var workflow = new BenchWorkflow(repo, selectors, new CurrentBenchResolver(repo, selectors));

        return new BenchCommand(workflow, _formatterFactory, authenticationProvider: authentication,
            repository: authentication is null ? null : Substitute.For<IWorkItemRepository>())
        {
            ResolveBrowserBindingAsync = resolveBinding,
        };
    }

    [Fact]
    public async Task ManagedList_ReportsStableTargetsFullPinKindsAndSavedQueryScopeWithoutSelectingCreatedBench()
    {
        var command = CreateCommand();
        var benches = new SqliteBenchRepository(_benchStore);
        var savedDefault = await benches.GetOrCreateDefaultAsync([BenchSelector.ForCurrentSprintCanonical("saved@example.test")]);
        await StdoutCapture.RunAsync(() => command.CreateAsync("Reviewed arrangement"));
        var target = (await benches.GetByNameAsync("Reviewed arrangement"))!;
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForItem(42));
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForSubtree(42));
        await benches.AddSelectorAsync(target.Id, new BenchQueryRule("Saved owner", null,
            [new("Project\\Team", false)], [BenchQueryRule.ParseSprint("@Current+1")]).ToSelector());
        target = (await benches.GetByNameAsync(target.Name))!;

        var (exit, output) = await StdoutCapture.RunAsync(() => command.ListAsync("json", includeManagement: true));

        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        root.GetProperty("version").GetInt32().ShouldBe(1);
        root.GetProperty("currentBenchId").GetString().ShouldBe(savedDefault.Id.ToString(CultureInfo.InvariantCulture));
        var records = root.GetProperty("benches").EnumerateArray().ToArray();
        var named = records.Single(bench => bench.GetProperty("name").GetString() == target.Name);
        named.GetProperty("id").GetString().ShouldBe(target.Id.ToString(CultureInfo.InvariantCulture));
        named.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
        named.GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        named.GetProperty("contentsDigest").GetString().ShouldBe(BenchQueryRule.ContentsDigest(target.Selectors));
        named.GetProperty("pins").EnumerateArray()
            .Select(pin => (pin.GetProperty("id").GetInt32(), pin.GetProperty("mode").GetString()))
            .ShouldBe([(42, "single"), (42, "tree")]);
        var query = named.GetProperty("queries")[0].GetString()!;
        query.ShouldContain("Saved owner");
        query.ShouldContain("Project\\Team");
        query.ShouldContain("@Current+1");
        var defaultRecord = records.Single(bench => bench.GetProperty("isDefault").GetBoolean());
        defaultRecord.GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        defaultRecord.GetProperty("contentsDigest").GetString().ShouldBe(BenchQueryRule.ContentsDigest(savedDefault.Selectors));

        var (_, secondOutput) = await StdoutCapture.RunAsync(() => command.ListAsync("json", includeManagement: true));
        using var second = JsonDocument.Parse(secondOutput);
        second.RootElement.GetProperty("benches").EnumerateArray()
            .Single(bench => bench.GetProperty("name").GetString() == target.Name)
            .GetProperty("id").GetString().ShouldBe(named.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("create", "other-binding", "actor")]
    [InlineData("list", "other-binding", "actor")]
    [InlineData("switch", "other-binding", "actor")]
    [InlineData("delete", "other-binding", "actor")]
    [InlineData("create", "binding", "other-actor")]
    [InlineData("list", "binding", "other-actor")]
    [InlineData("switch", "binding", "other-actor")]
    [InlineData("delete", "binding", "other-actor")]
    public async Task Lifecycle_OriginMismatchRefusesEveryPathWithoutChangingBenches(
        string operation, string expectBinding, string expectIdentity)
    {
        var authentication = Substitute.For<IAuthenticationProvider, IConnectionOperationGuard>();
        ((IConnectionOperationGuard)authentication).AcquireOperationAsync(Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IDisposable>());
        var binding = new ResolvedConnectionBinding(new IdentityBinding("binding", "connection", "actor", 1),
            new AuthenticationIdentity("actor", "Fixture", "tenant", "object", "issuer", "host", "credential", null),
            "fixture", "fixture", 1,
            new ConnectionOperationSnapshot("org", "project", "team", "fingerprint", 1, "manifest", "config", "unicode", "config"));
        var command = CreateCommand(authentication, _ => Task.FromResult(binding));
        var benches = new SqliteBenchRepository(_benchStore);
        var target = (await benches.CreateAsync("existing"))!;
        var (exit, output) = await StdoutCapture.RunAsync(() => operation switch
        {
            "create" => command.CreateAsync("new", "json", expectBinding: expectBinding, expectIdentity: expectIdentity),
            "list" => command.ListAsync("json", includeManagement: true, expectBinding: expectBinding, expectIdentity: expectIdentity),
            "switch" => command.SwitchAsync(target.Name, "json", expectBench: target.Id.ToString(), expectBinding: expectBinding, expectIdentity: expectIdentity),
            "delete" => command.DeleteAsync(target.Name, target.Name, "json", expectBench: target.Id.ToString(),
                expectBinding: expectBinding, expectIdentity: expectIdentity, expectContents: BenchQueryRule.ContentsDigest(target.Selectors)),
            _ => throw new ArgumentException("Unknown fixture operation"),
        });

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        (await benches.GetAllAsync()).Select(bench => bench.Name).ShouldBe([target.Name]);
        (await benches.GetCurrentAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task GuardedCommands_RefuseMissingNativeAdmissionBeforeCreatingAnything()
    {
        var command = CreateCommand();
        var (exit, output) = await StdoutCapture.RunAsync(() => command.CreateAsync("new", "json", expectBinding: "binding"));
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        (await new SqliteBenchRepository(_benchStore).GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ManagedList_NonJsonOutputIsUsageErrorAndDoesNotCreateDefault()
    {
        var command = CreateCommand();
        var (exit, output) = await StdoutCapture.RunAsync(() => command.ListAsync(includeManagement: true));
        exit.ShouldBe(2);
        output.ShouldBeEmpty();
        (await new SqliteBenchRepository(_benchStore).GetAllAsync()).ShouldBeEmpty();
    }


    // ═══════════════════════════════════════════════════════════════
    //  ADO #149 — switching, and what a SCRIPT sees when a name is wrong
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Switch_ToAnExistingBench_ReturnsZeroAndTheListingFollows()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var (result, _) = await StdoutCapture.RunAsync(() => cmd.SwitchAsync("release blockers"));
        result.ShouldBe(0);

        var (_, stdout) = await StdoutCapture.RunAsync(() => cmd.ListAsync());
        stdout.ShouldContain("Current: release blockers");
    }

    /// <summary>
    /// 🔴 The acceptance sentence for a SCRIPT: a non-zero exit, so its pipeline stops rather than
    /// proceeding against the wrong list. The exit code is the contract — a message alone is
    /// invisible to `set -e`.
    /// </summary>
    [Fact]
    public async Task Switch_ToAnUnknownBench_ExitsNonZero()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var (result, _) = await StdoutCapture.RunAsync(() => cmd.SwitchAsync("relase blockers"));

        result.ShouldNotBe(0);
    }

    [Fact]
    public async Task Switch_ToAnUnknownBench_CreatesNothing()
    {
        var cmd = CreateCommand();

        await StdoutCapture.RunAsync(() => cmd.SwitchAsync("relase blockers"));

        // Asserted through the LISTING, which is what a script inspects before acting: if the typo
        // had been adopted as a new Bench, it would show up here looking exactly like a real one.
        var (_, stdout) = await StdoutCapture.RunAsync(() => cmd.ListAsync());
        stdout.ShouldNotContain("relase blockers");
    }

    [Fact]
    public async Task Switch_ToAnUnknownBench_SaysWhatWasAskedForAndWhatToDo()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        try
        {
            await cmd.SwitchAsync("relase blockers");
        }
        finally
        {
            Console.SetError(original);
        }

        var message = stderr.ToString();
        message.ShouldContain("relase blockers");     // what was asked for
        message.ShouldContain("release blockers");    // what exists
        message.ShouldContain("bench create");        // what to do
    }

    [Fact]
    public async Task Switch_ToTheDefault_ReturnsZero_OnAFreshStore()
    {
        var cmd = CreateCommand();
        var (result, _) = await StdoutCapture.RunAsync(() => cmd.SwitchAsync(Bench.DefaultName));
        result.ShouldBe(0);
    }

    [Fact]
    public async Task Create_ValidName_ReturnsZeroAndSaysSo()
    {
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.CreateAsync("release blockers"));

        result.ShouldBe(0);
        stdout.ShouldContain("release blockers");
    }

    [Fact]
    public async Task Create_NameAlreadyTaken_ReturnsNonZero()
    {
        var cmd = CreateCommand();
        (await cmd.CreateAsync("release blockers")).ShouldBe(0);

        var second = await StdoutCapture.RunAsync(() => cmd.CreateAsync("release blockers"));

        // Non-zero, so a script that creates before acting finds out rather than proceeding
        // against a Bench somebody else's command made.
        second.result.ShouldNotBe(0);
    }

    [Fact]
    public async Task Create_BlankName_ReturnsNonZero()
    {
        var cmd = CreateCommand();
        var (result, _) = await StdoutCapture.RunAsync(() => cmd.CreateAsync("   "));
        result.ShouldNotBe(0);
    }

    [Fact]
    public async Task List_HumanOutput_MarksTheCurrentBench()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain(Bench.DefaultName);
        stdout.ShouldContain("release blockers");
        stdout.ShouldContain($"Current: {Bench.DefaultName}");
    }

    [Fact]
    public async Task List_JsonOutput_NamesEveryBenchAndWhichIsCurrent()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ListAsync("json"));

        result.ShouldBe(0);

        // 🔴 Parsed, not substring-matched. An earlier version of this test asserted the payload
        // CONTAINED "current" and passed against a listing that had dropped the current marker
        // entirely — the word was matching the table's column header. Read the VALUE.
        using var doc = JsonDocument.Parse(stdout);
        var carrier = FindObjectWith(doc.RootElement, "current");
        carrier.ShouldNotBeNull("The listing carries no 'current' value.");
        carrier!.Value.GetProperty("current").GetString().ShouldBe(Bench.DefaultName);

        // A script checks what exists before acting, so every Bench has to be in the payload.
        stdout.ShouldContain("release blockers");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO #150 — deleting reports what it holds, and there is NO force flag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 🔴 What a SCRIPT sees when the Bench holds work: a NON-ZERO exit, so its pipeline stops
    /// rather than proceeding as if the Bench were gone. The exit code is the contract; a printed
    /// warning is invisible to `set -e`.
    /// </summary>
    [Fact]
    public async Task Delete_ABenchThatHoldsPins_ExitsNonZeroAndListsWhatItHolds()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");
        await cmd.SwitchAsync("release blockers");
        await PinAsync(111);

        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.DeleteAsync("release blockers"));

        result.ShouldNotBe(0);
        stdout.ShouldContain("111");                 // WHICH item, not just how many
        stdout.ShouldContain("release blockers");

        // And it really is still there — asserted through the listing, which is what a script
        // inspects, rather than through the store.
        var (_, listing) = await StdoutCapture.RunAsync(() => cmd.ListAsync());
        listing.ShouldContain("release blockers");
    }

    [Fact]
    public async Task Delete_WithTheNameRetyped_ReturnsZeroAndTheBenchIsGone()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");
        await cmd.SwitchAsync("release blockers");
        await PinAsync(111);

        var (result, _) = await StdoutCapture.RunAsync(
            () => cmd.DeleteAsync("release blockers", confirm: "release blockers"));

        result.ShouldBe(0);

        var (_, listing) = await StdoutCapture.RunAsync(() => cmd.ListAsync());
        listing.ShouldNotContain("release blockers");
    }

    [Fact]
    public async Task Delete_AnUnknownBench_ExitsNonZeroAndSaysWhatWasAskedFor()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var stderr = new StringWriter();
        var original = Console.Error;
        Console.SetError(stderr);
        int result;
        try
        {
            result = await cmd.DeleteAsync("relase blockers");
        }
        finally
        {
            Console.SetError(original);
        }

        result.ShouldNotBe(0);
        var message = stderr.ToString();
        message.ShouldContain("relase blockers");    // what was asked for
        message.ShouldContain("release blockers");   // what exists
    }

    [Fact]
    public async Task Delete_TheDefaultBench_ExitsNonZero()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");

        var (result, _) = await StdoutCapture.RunAsync(() => cmd.DeleteAsync(Bench.DefaultName));

        result.ShouldNotBe(0);
    }

    /// <summary>
    /// The machine-readable half of the report: a script that asked for JSON has to be able to see
    /// WHICH items were at stake, not just that something went wrong.
    /// </summary>
    [Fact]
    public async Task Delete_ABenchThatHoldsPins_JsonOutput_NamesTheItemsHeld()
    {
        var cmd = CreateCommand();
        await cmd.CreateAsync("release blockers");
        await cmd.SwitchAsync("release blockers");
        await PinAsync(111);

        var (result, stdout) = await StdoutCapture.RunAsync(
            () => cmd.DeleteAsync("release blockers", outputFormat: "json"));

        result.ShouldNotBe(0);
        using var doc = JsonDocument.Parse(stdout);
        var carrier = FindObjectWith(doc.RootElement, "pinned");
        carrier.ShouldNotBeNull("The refusal payload carries no 'pinned' value.");
        carrier!.Value.GetProperty("pinned").GetString()!.ShouldContain("111");
    }

    /// <summary>
    /// 🔴 THE THIRD ACCEPTANCE CRITERION, asserted structurally: there is no force flag on
    /// <c>bench delete</c>. A flag needed routinely becomes a reflex, and the one time it matters
    /// the person types it without reading — that is how issue #271 recurs. This is a reflection
    /// test over the declared surface because a flag can only be added by declaring one, and it
    /// fails the moment somebody adds it "for scripts".
    /// </summary>
    [Fact]
    public void BenchDelete_HasNoForceFlag()
    {
        var command = typeof(TwigCommands).GetMethod(
            nameof(TwigCommands.BenchDelete), BindingFlags.Public | BindingFlags.Instance);
        command.ShouldNotBeNull();

        command!.GetParameters()
            .Any(p => string.Equals(p.Name, "force", StringComparison.OrdinalIgnoreCase))
            .ShouldBeFalse(
                "ADO #150: 'twig bench delete' must have NO force flag. The way past the report " +
                "is re-typing the Bench's name, which differs every time and so cannot become an " +
                "unread reflex.");

        // The scope control: the confirmation that DOES exist is a name, not a boolean. A bool
        // named anything else would be a force flag wearing a different label.
        var confirm = command.GetParameters()
            .SingleOrDefault(p => string.Equals(p.Name, "confirm", StringComparison.OrdinalIgnoreCase));
        confirm.ShouldNotBeNull("the confirmation is the Bench's name, re-typed");
        confirm!.ParameterType.ShouldBe(typeof(string));
    }

    /// <summary>
    /// Pins onto whatever Bench is current, through the same workflow the CLI's pin command uses,
    /// so these tests set up state the way a person would rather than by writing rows.
    /// </summary>
    private async Task PinAsync(int workItemId)
    {
        var repo = new SqliteBenchRepository(_benchStore);
        var selectors = new DefaultBenchSelectors(IdentityStubs.NewBound());
        var pin = new PinWorkflow(repo, selectors,
            new CurrentBenchResolver(repo, selectors));
        await pin.PinAsync(workItemId, includeSubtree: false);
    }

    /// <summary>
    /// Finds the object carrying <paramref name="property"/> anywhere in the payload, so the test
    /// asserts on the VALUE without pinning the renderer's envelope shape — which is presentation
    /// detail the spec deliberately leaves open.
    /// </summary>
    private static JsonElement? FindObjectWith(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return element;
            foreach (var child in element.EnumerateObject())
            {
                var found = FindObjectWith(child.Value, property);
                if (found is not null) return found;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var found = FindObjectWith(child, property);
                if (found is not null) return found;
            }
        }
        return null;
    }

    [Fact]
    public async Task List_OnAFreshStore_ShowsTheDefaultBenchNobodyCreated()
    {
        var cmd = CreateCommand();
        var (result, stdout) = await StdoutCapture.RunAsync(() => cmd.ListAsync());

        result.ShouldBe(0);
        stdout.ShouldContain(Bench.DefaultName);
    }
}
