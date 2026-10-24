using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class ShowDurableFieldOverlayTests
{
    private static ResolvedConnectionBinding BenchReadOrigin() => new(
        new IdentityBinding("binding-1", "connection-1", "identity-1", 1),
        new AuthenticationIdentity("identity-1", "Work", "tenant", "object", "issuer", "host", "credential", "user"),
        @"C:\checkout", "checkout-binding-pin", 1,
        new ConnectionOperationSnapshot("org", "Project", "Team", "fingerprint", 1, "portable", "preferences", "unicode", "portable"));

    private ServiceProvider ComposeBenchRead(
        Func<int, Task<ResolvedConnectionBinding>>? resolve = null)
    {
        var count = 0;
        var bindings = Substitute.For<IConnectionBindingService>();
        bindings.ResolveAsync(Arg.Any<TwigConfiguration>(), Arg.Any<TwigPaths>(), Arg.Any<CancellationToken>())
            .Returns(_ => resolve is null ? Task.FromResult(BenchReadOrigin()) : resolve(++count));
        return ComposeWorkspace(optedIn: true, replace: services => services.AddSingleton(bindings));
    }

    private static async Task<(int Exit, string Output, string Error)> ReadBenchV2(
        ServiceProvider services, string target, string? expected = null)
    {
        var result = 0;
        var (output, error) = await CaptureBothAsync(async () =>
        {
            result = await new TwigCommands(services).Workspace(output: "json", view: "tree",
                includeBrowser: true, bench: target, browserVersion: 2, expectBench: expected);
        });
        return (result, output, error);
    }

    private static async Task<(string Output, string Error)> CaptureBothAsync(Func<Task> action)
    {
        string error = "";
        var (_, output) = await StdoutCapture.RunAsync(async () =>
        {
            var (_, captured) = await StderrCapture.RunAsync(async () => { await action(); return 0; });
            error = captured;
            return 0;
        });
        return (output, error);
    }

    private static async Task<Bench> CreatePinnedBench(ServiceProvider services, string name, params int[] ids)
    {
        var benches = services.GetRequiredService<IBenchRepository>();
        var bench = (await benches.CreateAsync(name))!;
        foreach (var id in ids) await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(id));
        return (await benches.GetByNameAsync(name))!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BenchReadV2_ExplicitSelectionSurvivesPointerChange_AndHasNoReadSideEffects(bool byId)
    {
        ServiceProvider? runtime = null;
        Bench? other = null;
        Bench? final = null;
        using var services = runtime = ComposeBenchRead(async call =>
        {
            if (call == 2)
                await runtime!.GetRequiredService<IBenchRepository>().SetCurrentAsync(final!.Id);
            return BenchReadOrigin();
        });
        await SeedAsync(services, 42, "Mirror selected", "Durable edit must not leak");
        await SeedAsync(services, 43, "Other Bench", localTitle: null);
        var selected = await CreatePinnedBench(services, "Selected", 42);
        other = await CreatePinnedBench(services, "Other", 43);
        final = await CreatePinnedBench(services, "Third", 43);
        var benches = services.GetRequiredService<IBenchRepository>();
        await benches.SetCurrentAsync(other.Id);
        var before = DurableRows();
        var remote = services.GetRequiredService<IAdoWorkItemService>();
        remote.ClearReceivedCalls();
        var auth = services.GetRequiredService<IAuthenticationProvider>();
        auth.ClearReceivedCalls();

        var (exit, output, error) = await ReadBenchV2(services,
            byId ? selected.Id.ToString(CultureInfo.InvariantCulture) : selected.Name,
            selected.Id.ToString(CultureInfo.InvariantCulture));

        exit.ShouldBe(0, error);
        using var json = JsonDocument.Parse(output);
        var browser = json.RootElement.GetProperty("browser");
        browser.GetProperty("version").GetInt32().ShouldBe(2);
        browser.GetProperty("benchId").GetString().ShouldBe(selected.Id.ToString(CultureInfo.InvariantCulture));
        browser.GetProperty("members").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ShouldBe([42]);
        browser.GetProperty("members")[0].GetProperty("title").GetString().ShouldBe("Mirror selected");
        browser.GetProperty("origin").GetProperty("connectionRef").GetString().ShouldBe("connection-1");
        browser.GetProperty("origin").GetProperty("organization").GetString().ShouldBe("org");
        browser.GetProperty("origin").GetProperty("project").GetString().ShouldBe("Project");
        (await benches.GetCurrentAsync())!.Id.ShouldBe(final.Id);
        DurableRows().ShouldBe(before);
        remote.ReceivedCalls().ShouldBeEmpty();
        auth.ReceivedCalls().ShouldBeEmpty();
        (await services.GetRequiredService<IContextStore>().GetValueAsync("last_refreshed_at")).ShouldBeNull();
    }

    [Theory]
    [InlineData("connection")]
    [InlineData("binding")]
    [InlineData("identity")]
    [InlineData("checkout")]
    [InlineData("attachment")]
    [InlineData("selection")]
    [InlineData("generation")]
    [InlineData("fingerprint")]
    [InlineData("team")]
    public async Task BenchReadV2_WholeReadOriginChangesRefuseWithoutOutput(string change)
    {
        using var services = ComposeBenchRead(call =>
        {
            var origin = BenchReadOrigin();
            return Task.FromResult(call == 1 ? origin : change switch
            {
                "connection" => origin with { Binding = origin.Binding with { ConnectionRef = "other" } },
                "binding" => origin with { Binding = origin.Binding with { Revision = 2 } },
                "identity" => origin with { Identity = origin.Identity with { IdentityId = "other" } },
                "checkout" => origin with { WorktreeRoot = @"C:\other" },
                "attachment" => origin with { Operation = origin.Operation with { AttachmentRevision = 2 } },
                "selection" => origin with { SelectionRevision = 2 },
                "generation" => origin with { StorageGeneration = "other" },
                "fingerprint" => origin with { Operation = origin.Operation with { WorktreeFingerprint = "other" } },
                "team" => origin with { Operation = origin.Operation with { Team = "other" } },
                _ => throw new InvalidOperationException(change),
            });
        });
        await CreatePinnedBench(services, "Selected");
        var (exit, output, error) = await ReadBenchV2(services, "Selected");
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        using var failure = JsonDocument.Parse(error);
        failure.RootElement.GetProperty("error").GetString()!.ShouldContain("bench-origin-changed");
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("selectors")]
    [InlineData("replace")]
    public async Task BenchReadV2_WholeReadTargetChangesRefuse(string change)
    {
        ServiceProvider? runtime = null;
        Bench? selected = null;
        using var services = runtime = ComposeBenchRead(async call =>
        {
            if (call == 2)
            {
                var benches = runtime!.GetRequiredService<IBenchRepository>();
                if (change == "selectors")
                    await benches.AddSelectorAsync(selected!.Id, BenchSelector.ForItem(999));
                else
                {
                    await benches.DeleteAsync(selected!.Id);
                    if (change == "replace") await benches.CreateAsync(selected.Name);
                }
            }
            return BenchReadOrigin();
        });
        selected = await CreatePinnedBench(services, "Selected");
        var (exit, output, error) = await ReadBenchV2(services, "Selected");
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain(change == "delete" ? "bench-target-missing" : "bench-target-changed");
    }

    [Fact]
    public async Task BenchReadV2_MissingTargetIsNotCreated_AndIdNameCollisionIsRefused()
    {
        using var services = ComposeBenchRead();
        var (exit, output, error) = await ReadBenchV2(services, "Missing");
        exit.ShouldBe(1);
        output.ShouldBeEmpty();
        error.ShouldContain("bench-target-missing");
        var benches = services.GetRequiredService<IBenchRepository>();
        (await benches.GetAllAsync()).ShouldBeEmpty();
        var first = await CreatePinnedBench(services, "Named");
        await CreatePinnedBench(services, first.Id.ToString(CultureInfo.InvariantCulture));
        var ambiguous = await ReadBenchV2(services, first.Id.ToString(CultureInfo.InvariantCulture));
        ambiguous.Exit.ShouldBe(1);
        ambiguous.Error.ShouldContain("bench-target-ambiguous");
        ambiguous.Output.ShouldBeEmpty();
        var stale = await ReadBenchV2(services, "Named", "987654321");
        stale.Exit.ShouldBe(1);
        stale.Error.ShouldContain("bench-target-changed");
    }

    [Fact]
    public async Task BenchReadV2_DepthAndPartialCacheCountsNeverClaimSourceCompleteness()
    {
        using var services = ComposeBenchRead();
        await SeedAsync(services, 42, "Parent", localTitle: null);
        await SeedAsync(services, 43, "Child", localTitle: null, parentId: 42);
        await SeedAsync(services, 44, "Orphan", localTitle: null, parentId: 987);
        var bench = await CreatePinnedBench(services, "Partial", 42, 43, 44, 999);
        await services.GetRequiredService<IBenchRepository>().AddSelectorAsync(bench.Id, BenchSelector.ForSubtree(42));
        services.GetRequiredService<TwigConfiguration>().Display.TreeDepthDown = 0;
        var (exit, output, error) = await ReadBenchV2(services, "Partial");
        exit.ShouldBe(0, error);
        using var json = JsonDocument.Parse(output);
        var browser = json.RootElement.GetProperty("browser");
        var completeness = browser.GetProperty("completeness");
        completeness.GetProperty("knownMemberCount").GetInt32().ShouldBe(4);
        completeness.GetProperty("loadedMemberCount").GetInt32().ShouldBe(3);
        completeness.GetProperty("unloadedKnownMemberCount").GetInt32().ShouldBe(1);
        completeness.GetProperty("sourceMemberCount").ValueKind.ShouldBe(JsonValueKind.Null);
        completeness.GetProperty("unknownMemberCount").ValueKind.ShouldBe(JsonValueKind.Null);
        completeness.GetProperty("sourceComplete").GetBoolean().ShouldBeFalse();
        completeness.GetProperty("projectionComplete").GetBoolean().ShouldBeFalse();
        completeness.GetProperty("depthOmittedIds").EnumerateArray().Select(id => id.GetInt32()).ShouldContain(43);
        completeness.GetProperty("missingAncestorIds").EnumerateArray().Select(id => id.GetInt32()).ShouldContain(987);
        completeness.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString())
            .ShouldContain("selector-source-coverage-unknown");
        browser.GetProperty("members").GetArrayLength().ShouldBe(4);
        browser.GetProperty("members")[3].GetProperty("loaded").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task BenchReadV2_DefaultRebindsFromRegisteredMetadataWithoutIdentityHttpOrCurrentPointerWrites()
    {
        using var services = ComposeBenchRead();
        var benches = services.GetRequiredService<IBenchRepository>();
        var bench = await benches.GetOrCreateDefaultAsync([BenchSelector.ForCurrentSprint("Old display")]);
        await benches.AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        await SeedAsync(services, 42, "Raw default pin", localTitle: null);
        var iterations = services.GetRequiredService<IIterationService>();
        iterations.ClearReceivedCalls();
        var before = (await benches.GetByNameAsync("default"))!;
        var current = await benches.GetCurrentAsync();

        var (exit, output, error) = await ReadBenchV2(services, "default");

        exit.ShouldBe(0, error);
        iterations.ReceivedCalls().ShouldBeEmpty();
        (await benches.GetCurrentAsync()).ShouldBe(current);
        (await benches.GetByNameAsync("default"))!.Selectors.ShouldBe(before.Selectors);
        output.ShouldContain("Raw default pin");
        services.GetRequiredService<IAdoWorkItemService>().ReceivedCalls().ShouldBeEmpty();
        services.GetRequiredService<IAuthenticationProvider>().ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BenchReadV2_MissingCachedPrincipalRefusesOnlyTheDefaultWithoutRemoteLookup(bool isDefault)
    {
        using var services = ComposeBenchRead(_ => Task.FromResult(BenchReadOrigin() with
        { Identity = BenchReadOrigin().Identity with { AccountName = null } }));
        var benches = services.GetRequiredService<IBenchRepository>();
        if (isDefault) await benches.GetOrCreateDefaultAsync([]);
        else await CreatePinnedBench(services, "Custom");
        var (exit, output, error) = await ReadBenchV2(services, isDefault ? "default" : "Custom");
        exit.ShouldBe(isDefault ? 1 : 0, error);
        if (isDefault)
        {
            output.ShouldBeEmpty();
            error.ShouldContain("bench-bound-principal-unavailable");
        }
        services.GetRequiredService<IIterationService>().ReceivedCalls().ShouldBeEmpty();
        services.GetRequiredService<IAuthenticationProvider>().ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BenchReadV2_ExpectedOriginMismatchRefusesBeforeReading(bool identity)
    {
        using var services = ComposeBenchRead();
        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, error) = await StderrCapture.RunAsync(() => command.ExecuteAsync("json", view: "tree",
            includeBrowser: true, bench: "Missing", browserVersion: 2,
            expectIdentity: identity ? "other" : null, expectBinding: identity ? null : "other"));
        exit.ShouldBe(1);
        using var failure = JsonDocument.Parse(error);
        failure.RootElement.GetProperty("error").GetString()!.ShouldContain("connection or identity changed");
        (await services.GetRequiredService<IBenchRepository>().GetAllAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task BenchReadV2_EmptyMirrorIsNotAnEmptySourceClaim()
    {
        using var services = ComposeBenchRead();
        await CreatePinnedBench(services, "Empty");
        var (exit, output, error) = await ReadBenchV2(services, "Empty");
        exit.ShouldBe(0, error);
        using var json = JsonDocument.Parse(output);
        var browser = json.RootElement.GetProperty("browser");
        browser.GetProperty("roots").GetArrayLength().ShouldBe(0);
        browser.GetProperty("members").GetArrayLength().ShouldBe(0);
        browser.GetProperty("completeness").GetProperty("sourceComplete").GetBoolean().ShouldBeFalse();
        browser.GetProperty("completeness").GetProperty("sourceMemberCount").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(false, 2, "Selected", "tree", false, false, false, "json")]
    [InlineData(true, null, "Selected", "tree", false, false, false, "json")]
    [InlineData(true, 2, null, "tree", false, false, false, "json")]
    [InlineData(true, 1, "Selected", "tree", false, false, false, "json")]
    [InlineData(true, 2, "Selected", "table", false, false, false, "json")]
    [InlineData(true, 2, "Selected", "tree", true, false, false, "json")]
    [InlineData(true, 2, "Selected", "tree", false, true, false, "json")]
    [InlineData(true, 2, "Selected", "tree", false, false, true, "json")]
    [InlineData(true, 2, "Selected", "tree", false, false, false, "human")]
    [InlineData(true, 2, "Selected", "tree", false, false, false, "minimal")]
    public async Task BenchReadV2_InapplicableOptionsAreUsageErrors(bool include, int? version, string? target,
        string view, bool refresh, bool all, bool flat, string format)
    {
        using var services = ComposeBenchRead();
        var command = services.GetRequiredService<WorkspaceCommand>();
        var (exit, error) = await StderrCapture.RunAsync(() => command.ExecuteAsync(format, includeBrowser: include,
            browserVersion: version, bench: target, view: view, refresh: refresh, all: all, flat: flat));
        exit.ShouldBe(2);
        error.ShouldContain("bench-read-usage");
    }
}
