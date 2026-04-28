using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class ArtifactLinkCommandTests : IDisposable
{
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IAdoWorkItemService _adoService;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly StringWriter _stderr;
    private readonly StringWriter _stdout;
    private readonly TextWriter _originalOut;

    public ArtifactLinkCommandTests()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();

        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(),
            new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()),
            new MinimalOutputFormatter());

        _stderr = new StringWriter();
        _originalOut = Console.Out;
        _stdout = new StringWriter();
        Console.SetOut(_stdout);
    }

    private ArtifactLinkCommand CreateCommand()
    {
        var resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
        return new ArtifactLinkCommand(resolver, _adoService, _formatterFactory, _stderr);
    }

    private void SetActiveItem(int id, string title = "Test Item")
    {
        var item = new WorkItemBuilder(id, title).InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(id);
        _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(item);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Happy path — Hyperlink (http/https URL)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_HyperlinkUrl_CallsServiceAndOutputsLinked()
    {
        SetActiveItem(42);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCommand().ExecuteAsync("https://example.com/doc", "My Doc");

        result.ShouldBe(0);
        _stdout.ToString().ShouldContain("Linked");
        _stdout.ToString().ShouldContain("#42");
        await _adoService.Received(1).AddArtifactLinkAsync(
            42, "https://example.com/doc", "My Doc", Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Duplicate link — already linked
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_DuplicateLink_OutputsAlreadyLinkedAndReturnsZero()
    {
        SetActiveItem(42);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await CreateCommand().ExecuteAsync("https://example.com/doc");

        result.ShouldBe(0);
        _stdout.ToString().ShouldContain("Already linked");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Active item resolution — no --id flag
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoIdFlag_UsesActiveItemResolver()
    {
        SetActiveItem(99);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCommand().ExecuteAsync("https://example.com");

        result.ShouldBe(0);
        await _contextStore.Received(1).GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddArtifactLinkAsync(
            99, "https://example.com", null, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  --id override — bypasses active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_WithIdOverride_ResolvesById()
    {
        // Set up work item 42 in the repo (but NOT as active)
        var item = new WorkItemBuilder(42, "Target Item").InState("Active").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCommand().ExecuteAsync("https://example.com", id: 42);

        result.ShouldBe(0);
        // Should NOT have called GetActiveWorkItemIdAsync since --id was provided
        await _contextStore.DidNotReceive().GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
        await _adoService.Received(1).AddArtifactLinkAsync(
            42, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  No active item — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_NoActiveItem_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await CreateCommand().ExecuteAsync("https://example.com");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("No active work item");
        await _adoService.DidNotReceive().AddArtifactLinkAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Item not found — error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_ItemNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Not found"));

        var result = await CreateCommand().ExecuteAsync("https://example.com", id: 999);

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("#999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ADO error — service throws
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteAsync_AdoThrows_ReturnsErrorWithMessage()
    {
        SetActiveItem(42);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var result = await CreateCommand().ExecuteAsync("https://example.com");

        result.ShouldBe(1);
        _stderr.ToString().ShouldContain("Link failed");
        _stderr.ToString().ShouldContain("Network error");
    }

    // ═══════════════════════════════════════════════════════════════
    //  All output formats
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    [InlineData("json-compact")]
    [InlineData("minimal")]
    public async Task ExecuteAsync_AllFormats_Succeed(string format)
    {
        SetActiveItem(42);
        _adoService.AddArtifactLinkAsync(
                Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await CreateCommand().ExecuteAsync(
            "https://example.com", outputFormat: format);

        result.ShouldBe(0);
        _stdout.ToString().Length.ShouldBeGreaterThan(0);
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        _stderr.Dispose();
        _stdout.Dispose();
    }
}
