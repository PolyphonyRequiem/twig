using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Policy;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class PolicyCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"twig-policy-{Guid.NewGuid():N}");
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly SqliteWorkItemRepository _items;
    private readonly SqliteWorkItemLinkRepository _links;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly ITelemetryClient _telemetry = Substitute.For<ITelemetryClient>();
    private readonly PolicyCommand _command;
    private const string PolicyJson = """
        {"version":1,"rules":[{"id":"label","kind":"label-applicability",
        "prefix":"mode:","allowedTypes":["Leaf"],"exclusive":true}]}
        """;

    public PolicyCommandTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new TwigPaths(Path.Combine(_root, ".twig"), "", "", _root);
        File.WriteAllText(paths.PolicyPath, PolicyJson);
        _items = new(_store, new WorkItemMapper());
        _links = new(_store);
        _command = new(new(_items, _links), paths,
            new TwigConfiguration { Organization = "sample", Project = "project" },
            new RendererFactory(), _telemetry, _stdout, _stderr);
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_root, true);
    }

    private async Task Cache(string tags)
    {
        var item = new WorkItem
        {
            Id = 1, Type = WorkItemType.Parse("Leaf").Value, LastSyncedAt = DateTimeOffset.UnixEpoch,
        };
        item.UpdateField("System.Tags", tags);
        item.MarkSynced(5);
        await _items.SaveAsync(item);
    }

    [Theory]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    public async Task Assess_FormatsClearlyIdentifyScopeAndNoncompliance(string format)
    {
        await Cache("mode:a; mode:b");
        (await _command.AssessAsync("1", format)).ShouldBe(1);
        _stderr.ToString().ShouldBeEmpty();
        var text = _stdout.ToString();
        text.ShouldContain("noncompliant");
        if (format.StartsWith("json", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(text);
            document.RootElement.GetProperty("scope")[0].GetInt32().ShouldBe(1);
            document.RootElement.GetProperty("dependencyScope").GetArrayLength().ShouldBe(0);
            document.RootElement.GetProperty("evidenceLimits").EnumerateArray()
                .Select(limit => limit.GetString()).ShouldContain(
                    "Parent rules do not certify out-of-scope children, including children of a retyped parent.");
            var finding = document.RootElement.GetProperty("findings")[0];
            finding.GetProperty("code").GetString().ShouldBe("ExclusiveLabels");
            finding.GetProperty("evidence").GetProperty("labels").GetArrayLength().ShouldBe(2);
        }
        else text.ShouldContain("retain-label");
    }

    [Fact]
    public async Task CompliantPositiveControl_ExitsZero()
    {
        await Cache("");
        (await _command.AssessAsync("1", "json")).ShouldBe(0);
        using var document = JsonDocument.Parse(_stdout.ToString());
        document.RootElement.GetProperty("compliant").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("1,")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("one")]
    public async Task InvalidScope_IsUsageAndJsonError(string ids)
    {
        (await _command.AssessAsync(ids, "json")).ShouldBe(2);
        _stdout.ToString().ShouldBeEmpty();
        using var document = JsonDocument.Parse(_stderr.ToString());
        document.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("--ids");
    }

    [Fact]
    public async Task InvalidPolicy_IsNotSilentPermit()
    {
        File.WriteAllText(Path.Combine(_root, "twig.policy.json"), """{"version":99}""");
        (await _command.AssessAsync("1", "json")).ShouldBe(1);
        using var document = JsonDocument.Parse(_stderr.ToString());
        document.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("not supported");
    }

    [Fact]
    public async Task Reconcile_ProposalOnlyIsAcceptedByNativeParser_AndDoesNotAlterItemOrJournal()
    {
        await Cache("mode:a; mode:b; untouched");
        (await _command.AssessAsync("1", "json")).ShouldBe(1);
        using var assessment = JsonDocument.Parse(_stdout.ToString());
        var root = assessment.RootElement;
        _stdout.GetStringBuilder().Clear();
        var exit = await _command.ReconcileAsync("1", root.GetProperty("evidenceDigest").GetString()!,
            "f1", "retain-label-2", null, "json", proposalOnly: true);
        exit.ShouldBe(0, _stderr.ToString());
        var proposal = new PlanDocumentParser().Parse(_stdout.ToString());
        proposal.IsValid.ShouldBeTrue();
        proposal.Plan!.Operations.Count.ShouldBe(1);
        (await _items.GetByIdAsync(1))!.Fields["System.Tags"].ShouldBe("mode:a; mode:b; untouched");
        (await new SqlitePlanJournalRepository(_store).GetAsync(proposal.Digest!)).ShouldBeNull();
    }

    [Fact]
    public async Task Reconcile_EnvelopeContainsProjectedEvidenceAndNativeProposal()
    {
        await Cache("mode:a; mode:b");
        var assessment = await new PolicyReconciliationService(_items, _links).AssessAsync(
            new DeclaredPolicy { Rules = [new LabelApplicabilityRule
            {
                Id = "label", Prefix = "mode:", AllowedTypes = ["Leaf"], Exclusive = true,
            }] }, [1]);
        (await _command.ReconcileAsync("1", assessment.EvidenceDigest, "f1", "remove-labels", null, "json")).ShouldBe(0);
        using var result = JsonDocument.Parse(_stdout.ToString());
        result.RootElement.GetProperty("projected").GetProperty("compliant").GetBoolean().ShouldBeTrue();
        result.RootElement.GetProperty("before").GetProperty("compliant").GetBoolean().ShouldBeFalse();
        new PlanDocumentParser().Parse(result.RootElement.GetProperty("proposal").GetRawText()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task Reconcile_StaleSelection_EmitsNoProposalOnlyBytes()
    {
        await Cache("mode:a; mode:b");
        (await _command.ReconcileAsync("1", "old-evidence", "f1", "remove-labels", null, "json", true)).ShouldBe(1);
        _stdout.ToString().ShouldBeEmpty();
        using var error = JsonDocument.Parse(_stderr.ToString());
        error.RootElement.GetProperty("error").GetString().ShouldNotBeNull().ShouldContain("changed");
    }
}
