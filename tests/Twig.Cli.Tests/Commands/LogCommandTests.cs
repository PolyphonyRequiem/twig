using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for LogCommand (EPIC-006 ITEM-037).
/// </summary>
public class LogCommandTests
{
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IGitService _gitService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly HintEngine _hintEngine;

    public LogCommandTests()
    {
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _gitService = Substitute.For<IGitService>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(), new MinimalOutputFormatter());
        _hintEngine = new HintEngine(new DisplayConfig { Hints = false });
    }

    private LogCommand CreateCommand(IGitService? git = null) =>
        new(_workItemRepo, _formatterFactory, _hintEngine, git);

    private static WorkItem CreateWorkItem(int id, string title, string type = "User Story") => new()
    {
        Id = id,
        Type = WorkItemType.Parse(type).Value,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task Log_AnnotatesCommitsWithWorkItemInfo()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                "abc1234567890\tfeat(#42): add login",
                "def1234567890\tchore: update deps",
            });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Login feature"));

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync();
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("abc1234");
            output.ShouldContain("#42");
            output.ShouldContain("def1234");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Work item filter ────────────────────────────────────────────

    [Fact]
    public async Task Log_WorkItemFilter_ShowsOnlyMatchingCommits()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                "abc1234567890\tfeat(#42): add login",
                "def1234567890\tchore: update deps",
                "ghi1234567890\tfix(#99): fix bug",
            });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Login feature"));
        _workItemRepo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(99, "Bug fix", "Bug"));

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync(workItem: 42);
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("#42");
            output.ShouldNotContain("#99");
            output.ShouldNotContain("update deps");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── AB# pattern recognition ─────────────────────────────────────

    [Fact]
    public async Task Log_RecognizesABHashPattern()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[] { "abc1234567890\tResolves AB#42 login feature" });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Login feature"));

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync();
            result.ShouldBe(0);
            sw.ToString().ShouldContain("#42");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Count parameter ─────────────────────────────────────────────

    [Fact]
    public async Task Log_CountParameter_PassedToGit()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(5, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        var swErr = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(swErr);
        try
        {
            var result = await cmd.ExecuteAsync(count: 5);
            result.ShouldBe(0);
            await _gitService.Received().GetLogAsync(5, "%H%x09%s", Arg.Any<CancellationToken>());
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    // ── No git service ──────────────────────────────────────────────

    [Fact]
    public async Task Log_NoGitService_ReturnsError()
    {
        var cmd = CreateCommand(git: null);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Log_NotInWorkTree_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(false);

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    [Fact]
    public async Task Log_GitThrows_ReturnsError()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git log failed"));

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(1);
    }

    // ── Empty log ───────────────────────────────────────────────────

    [Fact]
    public async Task Log_EmptyLog_ReturnsSuccess()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(Array.Empty<string>());

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync();
        result.ShouldBe(0);
    }

    // ── JSON output ─────────────────────────────────────────────────

    [Fact]
    public async Task Log_JsonOutput_ContainsStructuredFields()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[] { "abc1234567890\tfeat(#42): add login" });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Login feature"));

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync(outputFormat: "json");
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("\"command\":\"log\"");
            output.ShouldContain("\"entries\"");
            output.ShouldContain("\"workItems\"");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Work item ID extraction ─────────────────────────────────────

    [Theory]
    [InlineData("feat(#42): add login", new[] { 42 })]
    [InlineData("Resolves AB#42", new[] { 42 })]
    [InlineData("fix(#10): and also #20", new[] { 10, 20 })]
    [InlineData("no work item refs", new int[0])]
    [InlineData("#123 and AB#456", new[] { 123, 456 })]
    public void ExtractWorkItemIds_VariousPatterns(string message, int[] expected)
    {
        var result = LogCommand.ExtractWorkItemIds(message);
        result.ShouldBe(expected.ToList());
    }

    // ── Minimal output ──────────────────────────────────────────────

    [Fact]
    public async Task Log_MinimalOutput_ShowsCompactEntries()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[] { "abc1234567890\tfeat(#42): add login" });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Login feature"));

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync(outputFormat: "minimal");
            result.ShouldBe(0);
            var output = sw.ToString();
            output.ShouldContain("abc1234");
            output.ShouldContain("#42");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }

    // ── Work item not in cache → still shows entry ──────────────────

    [Fact]
    public async Task Log_WorkItemNotInCache_StillShowsEntry()
    {
        _gitService.IsInsideWorkTreeAsync(Arg.Any<CancellationToken>()).Returns(true);
        _gitService.GetLogAsync(20, "%H%x09%s", Arg.Any<CancellationToken>())
            .Returns(new[] { "abc1234567890\tfeat(#42): add login" });
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var cmd = CreateCommand(_gitService);

        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var result = await cmd.ExecuteAsync();
            result.ShouldBe(0);
            sw.ToString().ShouldContain("abc1234");
        }
        finally
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        }
    }
}
