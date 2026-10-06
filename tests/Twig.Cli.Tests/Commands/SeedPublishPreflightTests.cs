using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>Exercises batch preflight through real SQLite, validation, orchestration and rendering.</summary>
public sealed class SeedPublishPreflightTests : IDisposable
{
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly string _twigDir = Path.Combine(Path.GetTempPath(), $"twig-preflight-{Guid.NewGuid():N}");
    private readonly TextWriter _originalOut = Console.Out;
    private readonly SqliteWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService = Substitute.For<IAdoWorkItemService>();
    private readonly SeedPublishCommand _publishCommand;
    private readonly SeedValidateCommand _validateCommand;

    public SeedPublishPreflightTests()
    {
        Directory.CreateDirectory(_twigDir);
        _workItemRepo = new SqliteWorkItemRepository(_store, new WorkItemMapper());
        var seedLinks = new SqliteSeedLinkRepository(_store);
        var rules = new FileSeedPublishRulesProvider(_twigDir);
        var formatters = new OutputFormatterFactory(new HumanOutputFormatter());
        var renderers = new RendererFactory();
        var orchestrator = new SeedPublishOrchestrator(
            _workItemRepo, _adoService, seedLinks, new SqliteWorkItemLinkRepository(_store),
            new SqlitePublishIdMapRepository(_store, new SqliteStagedIdentityRegistry(_store)),
            rules, new SqliteUnitOfWork(_store),
            new BacklogOrderer(_adoService, new SqliteFieldDefinitionStore(_store)),
            new SqlitePendingChangeStore(_store), null, ReferenceProfileBuilder.SprintPolicy());
        _publishCommand = new SeedPublishCommand(
            orchestrator, new SqliteContextStore(_store), formatters, renderers, _adoService);
        _validateCommand = new SeedValidateCommand(_workItemRepo, rules, seedLinks, formatters, renderers);
    }

    [Fact]
    public async Task BatchDryRun_AcLabelsVersionsAndDates_ValidatesAndPreviewsEverySeed()
    {
        const string prose = "<p>AC-1, AC-2, AC-3. Version v0.97.1-4; date 2026-1-2; value -3.5.</p>";
        for (var id = -1; id >= -4; id--)
            await _workItemRepo.SaveAsync(new WorkItemBuilder(id, $"Implement AC{id}").AsSeed()
                .WithField("System.Description", prose).Build());

        using var output = new StringWriter();
        Console.SetOut(output);
        (await _validateCommand.ExecuteAsync(outputFormat: "json")).ShouldBe(0);
        using (var validation = JsonDocument.Parse(output.ToString()))
        {
            validation.RootElement.GetProperty("passed").GetInt32().ShouldBe(4);
            validation.RootElement.GetProperty("total").GetInt32().ShouldBe(4);
        }
        output.GetStringBuilder().Clear();

        (await _publishCommand.ExecuteAsync(all: true, dryRun: true, outputFormat: "json")).ShouldBe(0);

        using var document = JsonDocument.Parse(output.ToString());
        var batch = document.RootElement;
        batch.GetProperty("hasErrors").GetBoolean().ShouldBeFalse();
        batch.GetProperty("preFlightErrors").GetArrayLength().ShouldBe(0);
        var results = batch.GetProperty("results").EnumerateArray().ToArray();
        results.Select(r => r.GetProperty("oldId").GetInt32()).Order().ShouldBe([-4, -3, -2, -1]);
        foreach (var result in results)
            result.GetProperty("status").GetString().ShouldBe("DryRun");
        (await _workItemRepo.GetSeedsAsync()).Select(s => s.Fields["System.Description"])
            .ShouldAllBe(value => value == prose);
        _adoService.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("json", true)]
    [InlineData("human", true)]
    [InlineData("minimal", true)]
    [InlineData("json", false)]
    [InlineData("human", false)]
    [InlineData("minimal", false)]
    public async Task BatchPreflight_ReportsEveryFailureWithoutClaimingNoSeeds(string format, bool dryRun)
    {
        await _workItemRepo.SaveAsync(new WorkItemBuilder(-1, "AC-1").AsSeed().WithParent(-99)
            .WithField("System.Description", "AC-2 depends on #-2.").Build());
        await _workItemRepo.SaveAsync(new WorkItemBuilder(-2, "Depends on seed...-1.").AsSeed().Build());
        using var output = new StringWriter();
        Console.SetOut(output);

        (await _publishCommand.ExecuteAsync(all: true, dryRun: dryRun, outputFormat: format)).ShouldBe(1);

        var text = output.ToString();
        text.ShouldNotContain("No seeds to publish");
        if (format == "json")
        {
            using var document = JsonDocument.Parse(text);
            var batch = document.RootElement;
            batch.GetProperty("hasErrors").GetBoolean().ShouldBeTrue();
            batch.GetProperty("results").GetArrayLength().ShouldBe(0);
            batch.GetProperty("cycleErrors").GetArrayLength().ShouldBe(0);
            var errors = batch.GetProperty("preFlightErrors").EnumerateArray()
                .Select(e => e.GetProperty("value").GetString()!).ToArray();
            errors.Length.ShouldBe(3);
            errors.ShouldContain(e => e.Contains("parent seed -99"));
            errors.ShouldContain(e => e.Contains("System.Description") && e.Contains("seed ID -2"));
            errors.ShouldContain(e => e.Contains("System.Title") && e.Contains("seed ID -1"));
        }
        else
        {
            text.ShouldContain("parent seed -99");
            text.ShouldContain("System.Description");
            text.ShouldContain("seed ID -2");
            text.ShouldContain("System.Title");
            text.ShouldContain("seed ID -1");
        }
        _adoService.ReceivedCalls().ShouldBeEmpty();
        (await _workItemRepo.GetSeedsAsync()).Select(s => s.Id).Order().ShouldBe([-2, -1]);
    }

    [Fact]
    public async Task BatchPreflight_ValidationResultsAndParentDiagnostics_AreBothVisible()
    {
        await _workItemRepo.SaveAsync(new WorkItemBuilder(-1, "").AsSeed().WithParent(-99).Build());
        using var output = new StringWriter();
        Console.SetOut(output);

        (await _publishCommand.ExecuteAsync(all: true, dryRun: true, outputFormat: "json")).ShouldBe(1);

        using var document = JsonDocument.Parse(output.ToString());
        var batch = document.RootElement;
        batch.GetProperty("results")[0].GetProperty("status").GetString().ShouldBe("ValidationFailed");
        batch.GetProperty("preFlightErrors")[0].GetProperty("value").GetString()!.ShouldContain("parent seed -99");
        _adoService.ReceivedCalls().ShouldBeEmpty();
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _store.Dispose();
        Directory.Delete(_twigDir, recursive: true);
    }
}
