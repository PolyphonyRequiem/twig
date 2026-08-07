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

    private BenchCommand CreateCommand()
    {
        _trackingRepo.GetAllTrackedAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TrackedItem>());

        var repo = new SqliteBenchRepository(_benchStore);
        var selectors = new DefaultBenchSelectors(null);
        var workflow = new BenchWorkflow(repo, selectors, new CurrentBenchResolver(repo, selectors));

        return new BenchCommand(workflow, _formatterFactory);
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
        var selectors = new DefaultBenchSelectors(userDisplayName: null);
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
