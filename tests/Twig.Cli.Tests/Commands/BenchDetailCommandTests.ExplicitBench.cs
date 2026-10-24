using System.Globalization;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Cli.Tests.Commands;

// Explicit-Bench document reads: cache-only, never the shared pointer, refusals carry stable codes.
public sealed partial class BenchDetailCommandTests
{
    private static string Id(Bench bench) => bench.Id.ToString(CultureInfo.InvariantCulture);

    private async Task<Bench> PinnedAsync(IBenchRepository benches, string name, params int[] ids)
    {
        var bench = (await benches.CreateAsync(name))!;
        foreach (var id in ids) await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(id));
        return (await benches.GetByNameAsync(name))!;
    }

    private async Task<(int Exit, string Output, string Error)> ReadExplicitAsync(IBenchRepository benches, int id,
        string target, string? expectBench = null, bool sync = false, bool document = true, string output = "json")
    {
        var stderr = new StringWriter();
        var (exit, text) = await CaptureAsync(() => CreateCommand(benches, stderr, _formLayouts)
            .ExecuteAsync(id, output: output, expectBench: expectBench, sync: sync, document: document, bench: target));
        return (exit, text, stderr.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitBench_SelectionIsStableDespitePointer_AndHasNoRemoteOrPointerSideEffects(bool byId)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var selected = await PinnedAsync(benches, "Selected", 42);
        var other = await PinnedAsync(benches, "Other", 43);
        await benches.SetCurrentAsync(other.Id);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Member"));

        var (exit, output, error) = await ReadExplicitAsync(benches, 42, byId ? Id(selected) : "Selected", Id(selected));

        exit.ShouldBe(0, error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        root.GetProperty("version").GetInt32().ShouldBe(2);
        root.GetProperty("benchId").GetString().ShouldBe(Id(selected));
        root.GetProperty("benchName").GetString().ShouldBe("Selected");
        root.GetProperty("observation").GetString().ShouldBe("raw-mirror");
        root.GetProperty("origin").GetProperty("bindingId").GetString().ShouldBe("binding");
        root.GetProperty("origin").GetProperty("organization").GetString().ShouldBe("org");
        root.GetProperty("membership").GetProperty("selected").GetBoolean().ShouldBeTrue();
        root.GetProperty("layoutStatus").GetString().ShouldBe("unavailable");
        (await benches.GetCurrentAsync())!.Id.ShouldBe(other.Id);
        _adoService.ReceivedCalls().ShouldBeEmpty();
        _formLayouts.ReceivedCalls().ShouldBeEmpty();
        _iterationService.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExplicitBench_CompletenessNeverClaimsSourceTotals()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await PinnedAsync(benches, "Partial", 42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Member"));

        var (exit, output, error) = await ReadExplicitAsync(benches, 42, "Partial");

        exit.ShouldBe(0, error);
        using var json = JsonDocument.Parse(output);
        var completeness = json.RootElement.GetProperty("completeness");
        completeness.GetProperty("sourceComplete").GetBoolean().ShouldBeFalse();
        completeness.GetProperty("sourceChildCount").ValueKind.ShouldBe(JsonValueKind.Null);
        completeness.GetProperty("cachedChildCount").GetInt32().ShouldBe(0);
        var reasons = completeness.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).ToArray();
        reasons.ShouldContain("cache-only");
        reasons.ShouldContain("children-total-unknown");
        reasons.ShouldContain("layout-not-read-cache-only");
    }

    [Fact]
    public async Task ExplicitBench_NonMemberIsRefusedWithoutReadingTheItem()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await PinnedAsync(benches, "Selected", 42);
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(99, "Elsewhere"));

        var (exit, output, error) = await ReadExplicitAsync(benches, 99, "Selected");

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain("bench-item-not-member");
        await _workItemRepo.DidNotReceive().GetByIdAsync(99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExplicitBench_MissingAmbiguousAndChangedTargetsAreRefused()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var named = await PinnedAsync(benches, "Named", 42);
        await PinnedAsync(benches, Id(named), 42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Member"));

        var missing = await ReadExplicitAsync(benches, 42, "Missing");
        missing.Exit.ShouldBe(1);
        missing.Output.ShouldBeEmpty();
        missing.Error.ShouldContain("bench-target-missing");
        (await benches.GetByNameAsync("Missing")).ShouldBeNull();

        var ambiguous = await ReadExplicitAsync(benches, 42, Id(named));
        ambiguous.Exit.ShouldBe(1);
        ambiguous.Output.ShouldBeEmpty();
        ambiguous.Error.ShouldContain("bench-target-ambiguous");

        var changed = await ReadExplicitAsync(benches, 42, "Named", "987654321");
        changed.Exit.ShouldBe(1);
        changed.Output.ShouldBeEmpty();
        changed.Error.ShouldContain("bench-target-changed");
    }

    [Fact]
    public async Task ExplicitBench_TargetChangedDuringReadRefusesAllOutput()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        var selected = await PinnedAsync(benches, "Selected", 42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await benches.AddSelectorAsync(selected.Id, BenchSelector.ForItem(77));
            return CreateWorkItem(42, "Member");
        });

        var (exit, output, error) = await ReadExplicitAsync(benches, 42, "Selected");

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain("bench-target-changed");
    }

    [Fact]
    public async Task ExplicitBench_OriginChangedDuringReadRefusesAllOutput()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await PinnedAsync(benches, "Selected", 42);
        var binding = await _bindings.ResolveAsync(_config, _paths);
        _bindings.ResolveAsync(Arg.Any<Twig.Infrastructure.Config.TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(_ => binding);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            binding = binding with { Identity = binding.Identity with { IdentityId = "different-actor" } };
            return CreateWorkItem(42, "Member");
        });

        var (exit, output, error) = await ReadExplicitAsync(benches, 42, "Selected");

        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain("bench-origin-changed");
    }

    [Theory]
    [InlineData(true, true, "json")]
    [InlineData(false, false, "json")]
    [InlineData(false, true, "human")]
    public async Task ExplicitBench_InapplicableModesAreUsageErrors(bool sync, bool document, string output)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await PinnedAsync(benches, "Selected", 42);

        var result = await ReadExplicitAsync(benches, 42, "Selected", sync: sync, document: document, output: output);

        result.Exit.ShouldBe(2);
        result.Output.ShouldBeEmpty();
        result.Error.ShouldContain("bench-read-usage");
        _adoService.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task CurrentOnlyDocument_IsUnchangedAndCarriesNoExplicitBenchFields()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var benches = new SqliteBenchRepository(store);
        await benches.GetOrCreateDefaultAsync([]);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(CreateWorkItem(42, "Item"));

        var (exit, output) = await CaptureAsync(() => CreateCommand(benches, new StringWriter(), _formLayouts)
            .ExecuteAsync(42, output: "json", document: true));

        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(output);
        foreach (var name in new[] { "origin", "observation", "membership", "completeness" })
            json.RootElement.TryGetProperty(name, out _).ShouldBeFalse(name);
    }
}
