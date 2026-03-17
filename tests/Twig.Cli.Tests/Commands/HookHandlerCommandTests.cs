using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class HookHandlerCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly TwigConfiguration _config;
    private readonly IGitService _gitService;
    private readonly string _tempDir;

    public HookHandlerCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _gitService = Substitute.For<IGitService>();
        _config = new TwigConfiguration();
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-hook-handler-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private HookHandlerCommand CreateCommand(IGitService? gitService = null) =>
        new(_contextStore, _workItemRepo, _config, gitService: gitService);

    private static WorkItem CreateWorkItem(int id, string title, string type = "Bug") => new()
    {
        Id = id,
        Type = WorkItemType.Parse(type).Value,
        Title = title,
        State = "Active",
        IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
        AreaPath = AreaPath.Parse("Project").Value,
    };

    // ── Unknown hook is silently ignored ────────────────────────────

    [Fact]
    public async Task Execute_UnknownHook_ReturnsZero()
    {
        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("unknown-hook", []);
        result.ShouldBe(0);
    }

    // ── post-checkout: skips file checkout (branch-flag=0) ──────────

    [Fact]
    public async Task PostCheckout_FileCheckout_SkipsContextSet()
    {
        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("post-checkout", ["abc123", "def456", "0"]);
        result.ShouldBe(0);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── post-checkout: too few args is a no-op ──────────────────────

    [Fact]
    public async Task PostCheckout_TooFewArgs_ReturnsZero()
    {
        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("post-checkout", ["abc123"]);
        result.ShouldBe(0);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── post-checkout: no git service is a no-op ────────────────────

    [Fact]
    public async Task PostCheckout_NoGitService_ReturnsZero()
    {
        var cmd = CreateCommand(gitService: null);
        var result = await cmd.ExecuteAsync("post-checkout", ["abc123", "def456", "1"]);
        result.ShouldBe(0);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── post-checkout: branch switch sets context ───────────────────

    [Fact]
    public async Task PostCheckout_BranchSwitch_SetsContext()
    {
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("bug/12345-fix-crash");
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(12345, "Fix crash"));

        var cmd = CreateCommand(_gitService);

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("post-checkout", ["abc123", "def456", "1"]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(12345, Arg.Any<CancellationToken>());
        errWriter.ToString().ShouldContain("Twig context → #12345");
    }

    // ── post-checkout: branch without work item ID is a no-op ───────

    [Fact]
    public async Task PostCheckout_BranchWithoutWorkItemId_DoesNotSetContext()
    {
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("main");

        var cmd = CreateCommand(_gitService);
        var result = await cmd.ExecuteAsync("post-checkout", ["abc123", "def456", "1"]);
        result.ShouldBe(0);

        await _contextStore.DidNotReceive().SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── post-checkout: work item not found still sets context ────────

    [Fact]
    public async Task PostCheckout_WorkItemNotFound_SetsContextWithIdOnly()
    {
        _gitService.GetCurrentBranchAsync(Arg.Any<CancellationToken>())
            .Returns("feature/999-some-thing");
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);

        var cmd = CreateCommand(_gitService);

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("post-checkout", ["abc123", "def456", "1"]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        await _contextStore.Received(1).SetActiveWorkItemIdAsync(999, Arg.Any<CancellationToken>());
        errWriter.ToString().ShouldContain("Twig context → #999");
        errWriter.ToString().ShouldNotContain("("); // No type/title info
    }

    // ── prepare-commit-msg: no active context is a no-op ────────────

    [Fact]
    public async Task PrepareCommitMsg_NoActiveContext_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "initial commit");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [msgFile]);
        result.ShouldBe(0);

        // File should be unchanged
        File.ReadAllText(msgFile).ShouldBe("initial commit");
    }

    // ── prepare-commit-msg: prefixes message when absent ────────────

    [Fact]
    public async Task PrepareCommitMsg_WritesPrefix()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "fix crash on startup");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [msgFile]);
        result.ShouldBe(0);

        var content = File.ReadAllText(msgFile);
        content.ShouldStartWith("#42 ");
        content.ShouldContain("fix crash on startup");
    }

    // ── prepare-commit-msg: skips when already prefixed ─────────────

    [Fact]
    public async Task PrepareCommitMsg_AlreadyPrefixed_DoesNotDuplicate()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "#42 fix crash on startup");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [msgFile]);
        result.ShouldBe(0);

        var content = File.ReadAllText(msgFile);
        // Should still start with exactly one #42
        content.ShouldBe("#42 fix crash on startup");
    }

    // ── prepare-commit-msg: skips when ID present mid-message ───────

    [Fact]
    public async Task PrepareCommitMsg_IdAlreadyInMessage_DoesNotPrefix()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(123);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "Fixes issue #123 — crash on login");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [msgFile]);
        result.ShouldBe(0);

        var content = File.ReadAllText(msgFile);
        content.ShouldBe("Fixes issue #123 — crash on login");
    }

    // ── prepare-commit-msg: no args is a no-op ──────────────────────

    [Fact]
    public async Task PrepareCommitMsg_NoArgs_ReturnsZero()
    {
        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", []);
        result.ShouldBe(0);
    }

    // ── prepare-commit-msg: longer ID in message still prefixes ──────

    [Fact]
    public async Task PrepareCommitMsg_LongerIdInMessage_StillPrefixes()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "Fix PR review for #420");

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [msgFile]);
        result.ShouldBe(0);

        var content = File.ReadAllText(msgFile);
        content.ShouldStartWith("#42 ");
        content.ShouldContain("Fix PR review for #420");
    }

    // ── prepare-commit-msg: missing file is a no-op ─────────────────

    [Fact]
    public async Task PrepareCommitMsg_MissingFile_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("prepare-commit-msg", [Path.Combine(_tempDir, "nonexistent")]);
        result.ShouldBe(0);
    }

    // ── commit-msg: warns when no work item reference ───────────────

    [Fact]
    public async Task CommitMsg_NoReference_WritesWarning()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "fix stuff");

        var cmd = CreateCommand();

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("commit-msg", [msgFile]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        errWriter.ToString().ShouldContain("Warning: commit message does not reference a work item");
    }

    // ── commit-msg: no warning when reference is present ────────────

    [Fact]
    public async Task CommitMsg_WithReference_NoWarning()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(42);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "#42 fix stuff");

        var cmd = CreateCommand();

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("commit-msg", [msgFile]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        errWriter.ToString().ShouldNotContain("Warning");
    }

    // ── commit-msg: matches small work item IDs (1–99) ──────────────

    [Fact]
    public async Task CommitMsg_SmallWorkItemId_NoWarning()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(7);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "#7 hotfix");

        var cmd = CreateCommand();

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("commit-msg", [msgFile]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        errWriter.ToString().ShouldNotContain("Warning");
    }

    // ── commit-msg: no active context is a no-op ────────────────────

    [Fact]
    public async Task CommitMsg_NoActiveContext_ReturnsZero()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var msgFile = Path.Combine(_tempDir, "COMMIT_EDITMSG");
        File.WriteAllText(msgFile, "no context");

        var cmd = CreateCommand();

        var originalErr = Console.Error;
        var errWriter = new StringWriter();
        Console.SetError(errWriter);
        try
        {
            var result = await cmd.ExecuteAsync("commit-msg", [msgFile]);
            result.ShouldBe(0);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        errWriter.ToString().ShouldBeEmpty();
    }

    // ── commit-msg: no args is a no-op ──────────────────────────────

    [Fact]
    public async Task CommitMsg_NoArgs_ReturnsZero()
    {
        var cmd = CreateCommand();
        var result = await cmd.ExecuteAsync("commit-msg", []);
        result.ShouldBe(0);
    }
}
