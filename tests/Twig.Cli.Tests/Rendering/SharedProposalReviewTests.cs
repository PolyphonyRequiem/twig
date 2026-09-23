using System.Text.Json;
using NSubstitute;
using Shouldly;
using Spectre.Console.Testing;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Plan;
using Twig.Rendering;
using Twig.RenderTree;
using Xunit;

namespace Twig.Cli.Tests.Rendering;

public sealed class SharedProposalReviewTests
{
    private const string Body = "<p>[red]EXACT description[/] 😀\nsecond line\u001b[2J</p>";
    private static ChangeProposalReviewModel Model() => new()
    {
        Digest = "review-digest", Workspace = new PlanWorkspace { Organization = "org", Project = "project" },
        AffectedItems = [new() { Id = 2, Type = "Task", Title = "Child [red]literal[/]", State = "Doing", Role = "target", ParentId = 1, Url = "https://example.test/2" }],
        ContextItems = [new() { Id = 1, Type = "Feature", Title = "Parent", State = "Doing", Role = "context" }],
        Operations = [new() { Ordinal = 0, OpId = "edit-child", Kind = "batch", Target = new() { WorkItemId = 2 }, Summary = "Edit child", Preconditions = [new() { Kind = "expectedRevision", Value = "4" }],
            Consequences = [new() { Kind = "field-set", Field = "System.Description", FieldLabel = "Description", To = Body, Before = new() { State = "value", Value = "old", Revision = 4 }, TextChange = ReviewTextChange.Measure("old", Body) },
                new() { Kind = "field-clear", Field = "System.AssignedTo", FieldLabel = "Assigned to", Before = new() { State = "absent", Revision = 4 } }] }],
        Blockers = [], AuthorizationChoices = ["apply", "revise", "decline"],
    };

    private static string Render(ChangeProposalReviewModel model, bool full = false, int width = 100, bool unicode = true, string icons = "unicode")
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Unicode = unicode;
        new SpectreNodeRenderer(console, new SpectreTheme(new DisplayConfig { Icons = icons }))
            .Render(new RenderTree.RenderTree(ChangeProposalReviewRenderer.Render(model, full)));
        return console.Output;
    }

    [Fact]
    public void BriefElidesOnlyDescriptionBodies_FullRetainsSafeExactSource()
    {
        var brief = Render(Model());
        brief.ShouldNotContain("EXACT description");
        brief.ShouldContain("Description");
        brief.ShouldContain("→ replace body");
        brief.ShouldContain("Assigned to");
        brief.ShouldContain("(clear)");
        brief.ShouldContain("−3 / +");
        brief.ShouldNotContain("expectedRevision");
        brief.ShouldNotContain("Change Proposal review");
        brief.ShouldNotContain("digest:");
        brief.ShouldNotContain("workspace:");
        brief.ShouldNotContain("(ad hoc)");
        brief.ShouldNotContain("[0]");
        brief.ShouldNotContain("op-");
        brief.ShouldNotContain("local-cache");
        brief.ShouldContain("--full");
        var full = Render(Model(), true, 200);
        full.ShouldContain("[red]EXACT description[/]");
        full.ShouldContain("😀");
        full.ShouldContain("\\u001b[2J");
        full.ShouldNotContain("\u001b[2J");
        full.ShouldNotContain("Changes");
        full.ShouldNotContain("Δ");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuilderLabelsReachHumanAndJson_WithExactConflictingReferences(bool full)
    {
        var items = Substitute.For<IWorkItemRepository>();
        items.GetByIdsAsync(Arg.Any<IEnumerable<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Twig.Domain.Aggregates.WorkItem>>([]));
        var fields = Substitute.For<IFieldDefinitionStore>();
        fields.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<Twig.Domain.ValueObjects.FieldDefinition>>([
            new("Custom.BusinessPriority", "Priority", "integer", false),
            new("Custom.SupportPriority", "Priority", "integer", false),
            new("System.State", "State", "string", false),
        ]));
        var definition = new PlanDefinition
        {
            Version = 1, Workspace = Model().Workspace,
            Operations = [
                new BatchOperation { Id = "business", WorkItemId = 2, ExpectedRevision = 4,
                    Fields = new Dictionary<string, string?> { ["Custom.BusinessPriority"] = "1", ["System.State"] = "Doing" } },
                new BatchOperation { Id = "support", WorkItemId = 3, ExpectedRevision = 5,
                    Fields = new Dictionary<string, string?> { ["Custom.SupportPriority"] = null } },
            ],
        };
        var model = await new ChangeProposalReviewModelBuilder(items, fields).BuildAsync(definition, "digest", [], [], true);
        var text = Render(model, full, 240);
        text.ShouldContain("Priority (Custom.BusinessPriority)");
        text.ShouldContain("Priority (Custom.SupportPriority)");
        text.ShouldContain("State");
        text.ShouldNotContain("State (System.State)");
        if (!full) text.ShouldNotContain("System.State");
        text.ShouldContain("(clear)");

        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(model));
        var output = new StringWriter();
        (await Command(lifecycle, output).PreviewAsync("p.json", "json-full", full, false, default)).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        var effects = json.RootElement.GetProperty("reviewModel").GetProperty("operations").EnumerateArray()
            .SelectMany(o => o.GetProperty("consequences").EnumerateArray()).ToArray();
        effects.Select(c => c.GetProperty("field").GetString()).ShouldBe(["Custom.BusinessPriority", "System.State", "Custom.SupportPriority"]);
        effects.Select(c => c.GetProperty("fieldLabel").GetString()).ShouldBe(["Priority (Custom.BusinessPriority)", "State", "Priority (Custom.SupportPriority)"]);
    }

    [Theory]
    [InlineData(36)]
    [InlineData(60)]
    [InlineData(100)]
    public void WrappedValuesHaveContinuousDivider_AndSiblingLabelsAlign(int width)
    {
        var text = Render(Model(), true, width);
        var valueLines = text.Split('\n').Where(l => l.Contains(" │ ")).ToArray();
        valueLines.Length.ShouldBeGreaterThanOrEqualTo(3); // Two fields plus at least one wrapped continuation.
        valueLines.Select(l => l.IndexOf(" │ ", StringComparison.Ordinal)).Distinct().Count().ShouldBe(1);
        text.Split('\n').ShouldAllBe(l => l.Length <= width + 2); // UTF-16 surrogate pair vs terminal cell count.
        text.ShouldContain("└─");
    }

    [Theory]
    [InlineData("unicode", true)]
    [InlineData("nerd", true)]
    [InlineData("unicode", false)]
    public void GlyphModesKeepIdentityAndEffects(string icons, bool unicode)
    {
        var text = Render(Model(), false, 160, unicode, icons);
        text.ShouldContain("#2"); text.ShouldContain("Doing"); text.ShouldContain("Task");
        text.ShouldContain("Assigned to");
        text.ShouldContain("(clear)");
        text.ShouldContain(unicode ? "│" : "|");
    }

    [Fact]
    public void InterleavedTargetsAndCyclesNeverReorderOrLoseOperations()
    {
        var model = Model();
        var op = model.Operations[0];
        static ReviewConsequence Effect(int id) => new() { Kind = "link-add", Relation = "related", OtherId = id };
        model = model with
        {
            AffectedItems = [.. model.AffectedItems, new() { Id = 1, Title = "Parent", Type = "Feature", Role = "target", ParentId = 2 }],
            Operations =
            [
                op with { Consequences = [Effect(901)] },
                op with { OpId = "edit-parent", Ordinal = 1, Target = new() { WorkItemId = 1 }, Consequences = [Effect(902)] },
                op with { OpId = "edit-child-again", Ordinal = 2, Consequences = [Effect(903)] },
            ],
        };
        var text = Render(model, width: 200);
        var first = text.IndexOf("add related link to #901", StringComparison.Ordinal);
        var second = text.IndexOf("add related link to #902", StringComparison.Ordinal);
        var third = text.IndexOf("add related link to #903", StringComparison.Ordinal);
        first.ShouldBeGreaterThanOrEqualTo(0);
        first.ShouldBeLessThan(second);
        second.ShouldBeLessThan(third);
    }

    [Fact]
    public void SixPreorderTargetsAppearOnce_WithChildrenAttachedToTailAncestor()
    {
        var model = Model();
        var items = Enumerable.Range(101, 6).Select(id => new ReviewAffectedItem
        {
            Id = id, Title = $"Item {id}", Type = "Task", State = "New", Role = "target",
            ParentId = id == 101 ? null : id == 102 ? 101 : 102,
        }).ToArray();
        model = model with { AffectedItems = items, ContextItems = [], Operations = items.Select((item, ordinal) =>
            model.Operations[0] with { Ordinal = ordinal, OpId = $"edit-{item.Id}", Target = new() { WorkItemId = item.Id } }).ToArray() };
        var roots = ChangeProposalReviewRenderer.Render(model).OfType<RenderNode.TreeView>().ToArray();
        roots.Length.ShouldBe(1);
        var feature = roots[0].Root.Children.ShouldHaveSingleItem();
        feature.Children.Count.ShouldBe(4);
        var text = Render(model, width: 200);
        foreach (var item in items)
            System.Text.RegularExpressions.Regex.Matches(text, $@"#{item.Id}\b").Count.ShouldBe(1);
        text.Split("--full").Length.ShouldBe(2);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(24)]
    [InlineData(60)]
    public void WideUnicodeLabelsUseCellWidths_WithoutOverflowOrNegativePadding(int width)
    {
        var console = new TestConsole();
        console.Profile.Width = width;
        console.Profile.Capabilities.Unicode = true;
        var block = new RenderNode.FieldBlock([
            new("状态名称", RenderCell.String("😀 old → 新")),
            new("Owner", RenderCell.String("a wrapped value")),
        ], 8);
        new SpectreNodeRenderer(console).Render(new RenderTree.RenderTree([block]));
        var lines = console.Output.Split('\n').Where(l => l.Length > 0).ToArray();
        static int Cells(string value) => Spectre.Console.Rendering.Segment.CellCount([new Spectre.Console.Rendering.Segment(value)]);
        foreach (var line in lines) Cells(line).ShouldBeLessThanOrEqualTo(width);
        var dividers = lines.Where(l => l.Contains(" │ ")).ToArray();
        dividers.Length.ShouldBeGreaterThan(1);
        dividers.Select(l => Cells(l[..l.IndexOf(" │ ", StringComparison.Ordinal)])).Distinct().Count().ShouldBe(1);
        foreach (var scalar in "状态名称😀新".EnumerateRunes()) console.Output.ShouldContain(scalar.ToString());
    }

    [Theory]
    [InlineData("json")]
    [InlineData("json-full")]
    [InlineData("json-compact")]
    public async Task CliJsonIsExactlyTheCanonicalSerializer_RegardlessOfDensity(string format)
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        var model = Model();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(model));
        foreach (var full in new[] { false, true })
        {
            var output = new StringWriter();
            var command = Command(lifecycle, output);
            (await command.PreviewAsync("p.json", format, full, false, default)).ShouldBe(0);
            using var actual = JsonDocument.Parse(output.ToString());
            using var expected = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
            JsonElement.DeepEquals(actual.RootElement.GetProperty("reviewModel"), expected.RootElement).ShouldBeTrue();
            actual.RootElement.GetProperty("reviewModel").GetProperty("operations")[0].GetProperty("consequences")[0].GetProperty("to").GetString().ShouldBe(Body);
        }
    }

    [Theory]
    [InlineData("always", "ansi", true)]
    [InlineData("never", "text", false)]
    public async Task IncludedPresentationRetainsExactModelAndBothDensitiesFromOnePreview(
        string color, string format, bool colored)
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        var model = Model();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(model));
        var output = new StringWriter();

        (await Command(lifecycle, output).PreviewAsync(
            "p.json", "json", full: false, interactive: false, ct: default,
            includeRendering: true, width: 80, color: color)).ShouldBe(0);

        using var actual = JsonDocument.Parse(output.ToString());
        using var expected = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        var root = actual.RootElement;
        JsonElement.DeepEquals(root.GetProperty("reviewModel"), expected.RootElement).ShouldBeTrue();
        root.GetProperty("digest").GetString().ShouldBe(model.Digest);
        var presentation = root.GetProperty("presentation");
        presentation.GetProperty("version").GetInt32().ShouldBe(1);
        presentation.GetProperty("format").GetString().ShouldBe(format);
        presentation.GetProperty("width").GetInt32().ShouldBe(80);
        var brief = presentation.GetProperty("brief").GetString()!;
        var full = presentation.GetProperty("full").GetString()!;
        brief.ShouldContain("Description");
        brief.ShouldNotContain("EXACT description");
        full.ShouldContain("EXACT description");
        full.ShouldContain("\\u001b[2J");
        full.ShouldNotContain("\u001b[2J");
        if (colored) brief.ShouldContain("\u001b[");
        else brief.ShouldNotContain("\u001b[");
        await lifecycle.Received(1).PreviewAsync("p.json", Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task InvalidProposalHasNoReviewFrameToAuthorize()
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("broken.json", Arg.Any<CancellationToken>()).Returns(new PlanPreviewResult
        {
            Digest = null, CanApply = false, Operations = [], PendingChanges = [],
            Issues = [new PlanValidationIssue { Code = PlanValidationCodes.JsonInvalid, Path = "", Message = "Invalid JSON" }],
        });
        var output = new StringWriter();

        (await Command(lifecycle, output).PreviewAsync(
            "broken.json", "json", full: false, interactive: false, ct: default,
            includeRendering: true, width: 100, color: "always")).ShouldBe(1);

        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("presentation").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("reviewModel").ValueKind.ShouldBe(JsonValueKind.Null);
        json.RootElement.GetProperty("issues")[0].GetProperty("code").GetString().ShouldBe(PlanValidationCodes.JsonInvalid);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("human", true, 80, "auto")]
    [InlineData("minimal", true, 80, "auto")]
    [InlineData("json", true, 19, "always")]
    [InlineData("json", true, 401, "always")]
    [InlineData("json", true, 80, "auto")]
    [InlineData("json", true, 80, "unsupported")]
    public async Task InvalidPresentationOptionsFailBeforePreview(
        string outputFormat, bool includeRendering, int width, string color)
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        var stderr = new StringWriter();
        var command = new PlanCommand(lifecycle, new(new HumanOutputFormatter()),
            new UnresolvedSessionSteeringModeProvider(), TimeProvider.System,
            stdout: new StringWriter(), stderr: stderr);

        (await command.PreviewAsync("p.json", outputFormat, full: false, interactive: false,
            ct: default, includeRendering: includeRendering, width: width, color: color)).ShouldBe(2);
        stderr.ToString().ShouldNotBeNullOrWhiteSpace();
        await lifecycle.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimalRetainsCanonicalModel_IncludingDeleteChoicesAndFields(bool full)
    {
        var model = Model();
        model = model with
        {
            Operations = [.. model.Operations, new()
            {
                Ordinal = 1, OpId = "delete-child", Kind = "delete", Target = new() { WorkItemId = 2 },
                Summary = "Delete child", Preconditions = [],
                Consequences = [new() { Kind = "work-item-delete", OtherId = 2 }],
            }],
            Blockers = [new() { Kind = "conflict", WorkItemId = 2, Detail = "Resolve before applying" }],
        };
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(model));
        var output = new StringWriter();
        (await Command(lifecycle, output).PreviewAsync("p.json", "minimal", full, false, default)).ShouldBe(0);

        var line = output.ToString().Split('\n').Single(l => l.StartsWith("reviewModel=", StringComparison.Ordinal));
        var actual = line["reviewModel=".Length..].TrimEnd('\r');
        actual.ShouldBe(ChangeProposalReviewModelJson.Serialize(model));
        using var json = JsonDocument.Parse(actual);
        var review = json.RootElement;
        review.GetProperty("operations")[0].GetProperty("consequences")[0].GetProperty("field").GetString().ShouldBe("System.Description");
        review.GetProperty("operations")[0].GetProperty("consequences")[0].GetProperty("to").GetString().ShouldBe(Body);
        review.GetProperty("operations")[1].GetProperty("consequences")[0].GetProperty("kind").GetString().ShouldBe("work-item-delete");
        review.GetProperty("authorizationChoices").EnumerateArray().Select(c => c.GetString()).ShouldBe(model.AuthorizationChoices);
        review.GetProperty("blockers")[0].GetProperty("detail").GetString().ShouldBe("Resolve before applying");
    }

    [Fact]
    public async Task DetailsBackCancelAndInvalidInputReuseOneObservation_AndNeverApply()
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(Model()));
        var output = new StringWriter();
        var cmd = new PlanCommand(lifecycle, new(new HumanOutputFormatter()), new UnresolvedSessionSteeringModeProvider(), TimeProvider.System, stdout: output)
        { IsReviewTerminal = () => true, ReviewInput = new StringReader("apply\ndetails\nback\ncancel\n") };
        (await cmd.PreviewAsync("p.json", "human", false, true, default)).ShouldBe(0);
        output.ToString().ShouldContain("EXACT description");
        output.ToString().ShouldContain("No authorization or apply occurs here");
        output.ToString().ShouldContain("Review closed. Not applied.");
        await lifecycle.Received(1).PreviewAsync("p.json", Arg.Any<CancellationToken>());
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData("human", false)]
    [InlineData("json", true)]
    public async Task InteractiveRejectsNonHumanOrNonTerminal_BeforePreview(string format, bool terminal)
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        var cmd = new PlanCommand(lifecycle, new(new HumanOutputFormatter()), new UnresolvedSessionSteeringModeProvider(), TimeProvider.System, stdout: new StringWriter(), stderr: new StringWriter())
        { IsReviewTerminal = () => terminal };
        (await cmd.PreviewAsync("p.json", format, false, true, default)).ShouldBe(2);
        await lifecycle.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Fact]
    public async Task EofClosesReviewWithoutAuthorization()
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.PreviewAsync("p.json", Arg.Any<CancellationToken>()).Returns(Preview(Model()));
        var cmd = new PlanCommand(lifecycle, new(new HumanOutputFormatter()), new UnresolvedSessionSteeringModeProvider(), TimeProvider.System, stdout: new StringWriter())
        { IsReviewTerminal = () => true, ReviewInput = new StringReader("") };
        (await cmd.PreviewAsync("p.json", "human", false, true, default)).ShouldBe(0);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default, default);
    }

    private static PlanCommand Command(IPlanLifecycleService lifecycle, StringWriter output) => new(lifecycle, new(new HumanOutputFormatter()), new UnresolvedSessionSteeringModeProvider(), TimeProvider.System, stdout: output);
    private static PlanPreviewResult Preview(ChangeProposalReviewModel model) => new() { Digest = model.Digest, CanApply = true, ReviewModel = model, Operations = [], Issues = [], PendingChanges = [] };
}
