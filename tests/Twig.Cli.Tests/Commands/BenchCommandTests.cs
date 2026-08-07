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

        var workflow = new BenchWorkflow(
            new SqliteBenchRepository(_benchStore),
            new DefaultBenchSelectors(_trackingRepo, userDisplayName: null));

        return new BenchCommand(workflow, _formatterFactory);
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
