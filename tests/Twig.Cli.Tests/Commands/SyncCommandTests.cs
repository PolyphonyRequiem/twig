using System.Net.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Tests for <c>twig sync</c>: flush-then-refresh sequence.
/// Verifies exit code logic, stderr output for flush failures,
/// JSON structured output, and phase ordering.
/// </summary>
public sealed class SyncCommandTests : IDisposable
{
    private readonly string _testDir;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly IIterationService _iterationService;
    private readonly IPendingChangeStore _pendingChangeStore;
    private readonly ProtectedCacheWriter _protectedCacheWriter;
    private readonly WorkingSetService _workingSetService;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly IProcessTypeStore _processTypeStore;
    private readonly IFieldDefinitionStore _fieldDefinitionStore;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly IPendingChangeFlusher _flusher;

    public SyncCommandTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"twig-sync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var twigDir = Path.Combine(_testDir, ".twig");
        Directory.CreateDirectory(twigDir);
        var configPath = Path.Combine(twigDir, "config");
        var dbPath = Path.Combine(twigDir, "twig.db");

        _config = new TwigConfiguration { Organization = "https://dev.azure.com/org", Project = "MyProject" };
        _paths = new TwigPaths(twigDir, configPath, dbPath);
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _iterationService = Substitute.For<IIterationService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, _pendingChangeStore);
        _syncCoordinator = new SyncCoordinator(_workItemRepo, _adoService, _protectedCacheWriter, _pendingChangeStore, 30);
        _workingSetService = new WorkingSetService(_contextStore, _workItemRepo, _pendingChangeStore, _iterationService, null);
        _processTypeStore = Substitute.For<IProcessTypeStore>();
        _fieldDefinitionStore = Substitute.For<IFieldDefinitionStore>();
        _flusher = Substitute.For<IPendingChangeFlusher>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());

        // Default iteration service stubs for RefreshCommand
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse("Project\\Sprint 1").Value);
        _iterationService.GetWorkItemTypeAppearancesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeAppearance>());
        _iterationService.GetWorkItemTypesWithStatesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<WorkItemTypeWithStates>());
        _iterationService.GetProcessConfigurationAsync(Arg.Any<CancellationToken>())
            .Returns(new ProcessConfigurationData());
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
    }

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }

    private RefreshCommand CreateRefreshCommand(TextWriter? stderr = null) =>
        new(_contextStore, _workItemRepo, _adoService, _iterationService,
            _pendingChangeStore, _protectedCacheWriter, _config, _paths, _processTypeStore, _fieldDefinitionStore,
            _formatterFactory, _workingSetService, _syncCoordinator, stderr: stderr);

    private SyncCommand CreateSyncCommand(TextWriter? stderr = null) =>
        new(_flusher, CreateRefreshCommand(stderr), _formatterFactory, stderr);

    private static async Task<string> CaptureStdoutAsync(Func<Task> action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try { await action(); }
        finally { Console.SetOut(originalOut); }
        return stdout.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_NoPendingChanges_ReturnsZero()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    [Fact]
    public async Task Sync_SuccessfulFlushAndRefresh_ReturnsZero()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(2, 3, 1, []));

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Flush failure scenarios
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_FlushFailures_ReturnsNonZero()
    {
        var failures = new List<FlushItemFailure> { new(42, "Auth expired") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 1, 0, failures));

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync();

        result.ShouldBe(1);
    }

    [Fact]
    public async Task Sync_FlushFailures_WritesErrorsToStderr()
    {
        var failures = new List<FlushItemFailure> { new(42, "Auth expired"), new(99, "Not found") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, failures));

        var stderr = new StringWriter();
        var cmd = CreateSyncCommand(stderr);
        await cmd.ExecuteAsync();

        var output = stderr.ToString();
        output.ShouldContain("#42");
        output.ShouldContain("Auth expired");
        output.ShouldContain("#99");
        output.ShouldContain("Not found");
    }

    [Fact]
    public async Task Sync_FlushFailures_StillCallsRefresh()
    {
        var failures = new List<FlushItemFailure> { new(42, "Error") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, failures));

        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync();

        // Verify refresh was called (it queries WIQL as its first significant operation)
        await _adoService.Received(1).QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Phase ordering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_FlushCalledBeforeRefresh()
    {
        var callOrder = new List<string>();

        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("flush");
                return new FlushResult(0, 0, 0, []);
            });

        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("refresh");
                return Array.Empty<int>();
            });

        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync();

        callOrder.ShouldBe(new[] { "flush", "refresh" });
    }

    // ═══════════════════════════════════════════════════════════════
    //  Force flag passthrough
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_ForceFlag_PassedToRefresh()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        // Set up dirty items to verify force behavior
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { 1 });

        var item = new WorkItem
        {
            Id = 1, Title = "Item", Type = WorkItemType.Task, State = "New",
            IterationPath = IterationPath.Parse("Project\\Sprint 1").Value,
            AreaPath = AreaPath.Parse("Project").Value
        };
        _adoService.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { item });

        var cmd = CreateSyncCommand();
        var result = await cmd.ExecuteAsync(force: true);

        // With --force, items should be saved directly to repo (not through protected writer)
        await _workItemRepo.Received().SaveBatchAsync(Arg.Any<IReadOnlyList<WorkItem>>(), Arg.Any<CancellationToken>());
        result.ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  JSON output
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_JsonFormat_EmitsStructuredOutput()
    {
        var failures = new List<FlushItemFailure> { new(42, "Test error") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(3, 5, 2, failures));

        var cmd = CreateSyncCommand();

        var output = await CaptureStdoutAsync(() => cmd.ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"flush\"");
        output.ShouldContain("\"flushed\": 3");
        output.ShouldContain("\"fieldChangesPushed\": 5");
        output.ShouldContain("\"notesPushed\": 2");
        output.ShouldContain("\"failed\": 1");
        output.ShouldContain("\"itemId\": 42");
        output.ShouldContain("\"refresh\"");
        output.ShouldContain("\"exitCode\": 0");
    }

    [Fact]
    public async Task Sync_JsonFormat_NoFailures_OmitsFailuresArray()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 2, 0, []));

        var cmd = CreateSyncCommand();

        var output = await CaptureStdoutAsync(() => cmd.ExecuteAsync(outputFormat: "json"));

        output.ShouldContain("\"flush\"");
        output.ShouldNotContain("\"failures\"");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Output format passthrough
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_OutputFormatPassedToFlusher()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync(outputFormat: "json");

        await _flusher.Received(1).FlushAllAsync("json", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Flush-all verification (FlushAllAsync, not FlushAsync)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_AlwaysCallsFlushAllAsync_NotFlushAsync()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));

        var cmd = CreateSyncCommand();
        await cmd.ExecuteAsync();

        await _flusher.Received(1).FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _flusher.DidNotReceive().FlushAsync(
            Arg.Any<IReadOnlyList<int>>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Flush throws exception
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_FlushThrows_RefreshNotCalled()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Push failed"));

        var cmd = CreateSyncCommand();

        await Should.ThrowAsync<InvalidOperationException>(() => cmd.ExecuteAsync());

        // Refresh was never reached because flush threw
        await _adoService.DidNotReceive().QueryByWiqlAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Fetch failure after flush
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_RefreshThrows_ExceptionPropagatesAfterSuccessfulFlush()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 2, 0, []));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO query failed"));

        var cmd = CreateSyncCommand();

        await Should.ThrowAsync<InvalidOperationException>(() => cmd.ExecuteAsync());

        // Flush was called before the exception
        await _flusher.Received(1).FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Both flush and fetch fail
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_FlushFailuresAndRefreshThrows_FlushErrorsWrittenBeforeRefreshException()
    {
        var failures = new List<FlushItemFailure> { new(42, "Auth expired") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, failures));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("ADO query failed"));

        var stderr = new StringWriter();
        var cmd = CreateSyncCommand(stderr);

        await Should.ThrowAsync<InvalidOperationException>(() => cmd.ExecuteAsync());

        // Flush errors were still written to stderr before the refresh exception
        var output = stderr.ToString();
        output.ShouldContain("#42");
        output.ShouldContain("Auth expired");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Offline fallback (HttpRequestException)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sync_OfflineDuringFlush_HttpRequestExceptionPropagates()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network unreachable"));

        var cmd = CreateSyncCommand();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => cmd.ExecuteAsync());
        ex.Message.ShouldContain("Network unreachable");
    }

    [Fact]
    public async Task Sync_OfflineDuringRefresh_HttpRequestExceptionPropagatesAfterFlush()
    {
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(0, 0, 0, []));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var cmd = CreateSyncCommand();

        var ex = await Should.ThrowAsync<HttpRequestException>(() => cmd.ExecuteAsync());
        ex.Message.ShouldContain("Connection refused");

        // Flush was still called successfully before the offline error
        await _flusher.Received(1).FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sync_OfflineDuringRefresh_FlushErrorsStillVisible()
    {
        var failures = new List<FlushItemFailure> { new(10, "Partial push failed") };
        _flusher.FlushAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FlushResult(1, 1, 0, failures));
        _adoService.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Service unavailable"));

        var stderr = new StringWriter();
        var cmd = CreateSyncCommand(stderr);

        await Should.ThrowAsync<HttpRequestException>(() => cmd.ExecuteAsync());

        // Flush errors were logged to stderr even though refresh also failed
        stderr.ToString().ShouldContain("#10");
        stderr.ToString().ShouldContain("Partial push failed");
    }
}
