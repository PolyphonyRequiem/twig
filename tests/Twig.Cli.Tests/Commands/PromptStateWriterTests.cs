using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public class PromptStateWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _twigDir;
    private readonly string _originalDir;
    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly IProcessTypeStore _processTypeStore;

    public PromptStateWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"twig-psw-test-{Guid.NewGuid():N}");
        _twigDir = Path.Combine(_tempDir, ".twig");
        Directory.CreateDirectory(_twigDir);
        _originalDir = Directory.GetCurrentDirectory();

        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _processTypeStore = Substitute.For<IProcessTypeStore>();
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDir);
        try { Directory.Delete(_tempDir, true); } catch { /* best effort cleanup */ }
    }

    private PromptStateWriter CreateWriter(TwigConfiguration? config = null)
    {
        config ??= new TwigConfiguration();
        var paths = new TwigPaths(_twigDir, Path.Combine(_twigDir, "config"), Path.Combine(_twigDir, "twig.db"));
        return new PromptStateWriter(_contextStore, _workItemRepo, config, paths, _processTypeStore);
    }

    private static WorkItem CreateWorkItem(int id, string type, string title, string state, bool isDirty = false)
    {
        var wi = new WorkItem
        {
            Id = id,
            Type = WorkItemType.Parse(type).Value,
            Title = title,
            State = state,
        };
        if (isDirty)
            wi.SetDirty();
        return wi;
    }

    private string PromptJsonPath => Path.Combine(_twigDir, "prompt.json");

    // ── (a) writes valid JSON when active item exists ──────────────────

    [Fact]
    public async Task WritesValidJson_WhenActiveItemExists()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(12345);
        _workItemRepo.GetByIdAsync(12345, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(12345, "Epic", "Implement login", "Active"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        File.Exists(PromptJsonPath).ShouldBeTrue();
        var json = File.ReadAllText(PromptJsonPath);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("id").GetInt32().ShouldBe(12345);
        root.GetProperty("type").GetString().ShouldBe("Epic");
        root.GetProperty("typeBadge").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("title").GetString().ShouldBe("Implement login");
        root.GetProperty("state").GetString().ShouldBe("Active");
        root.GetProperty("stateCategory").GetString().ShouldBe("InProgress");
        root.TryGetProperty("isDirty", out _).ShouldBeTrue();
        root.TryGetProperty("typeColor", out _).ShouldBeTrue();
        root.TryGetProperty("stateColor", out _).ShouldBeTrue();
        root.TryGetProperty("branch", out _).ShouldBeTrue();
        root.TryGetProperty("generatedAt", out _).ShouldBeTrue();
        root.TryGetProperty("text", out _).ShouldBeTrue();
    }

    // ── (b) writes {} when no active item ──────────────────────────────

    [Fact]
    public async Task WritesEmptyObject_WhenNoActiveItem()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        File.Exists(PromptJsonPath).ShouldBeTrue();
        var json = File.ReadAllText(PromptJsonPath);
        json.ShouldBe("{}");
    }

    // ── (c) correct typeBadge for unicode mode ─────────────────────────

    [Fact]
    public async Task TypeBadge_Unicode_Epic()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Epic", "Test", "Active"));

        var config = new TwigConfiguration { Display = new DisplayConfig { Icons = "unicode" } };
        var writer = CreateWriter(config);
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("typeBadge").GetString().ShouldBe("◆");
    }

    // ── (d) correct typeBadge for nerd mode ────────────────────────────

    [Fact]
    public async Task TypeBadge_Nerd_Epic()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Epic", "Test", "Active"));

        var config = new TwigConfiguration
        {
            Display = new DisplayConfig { Icons = "nerd" },
            TypeAppearances = [new TypeAppearanceConfig { Name = "Epic", IconId = "icon_crown" }]
        };
        var writer = CreateWriter(config);
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        var badge = doc.RootElement.GetProperty("typeBadge").GetString();
        badge.ShouldNotBe("◆"); // Not the unicode badge
        // ResolveTypeBadge pads BMP PUA with trailing space via NormalizeBadgeWidth
        var expectedRaw = IconSet.GetIconByIconId("nerd", "icon_crown");
        badge.ShouldBe(expectedRaw + " ");
    }

    // ── (e) typeColor from display.typeColors config ───────────────────

    [Fact]
    public async Task TypeColor_FromDisplayTypeColors()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Epic", "Test", "Active"));

        var config = new TwigConfiguration
        {
            Display = new DisplayConfig
            {
                TypeColors = new Dictionary<string, string> { ["Epic"] = "#8B00FF" }
            }
        };
        var writer = CreateWriter(config);
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("typeColor").GetString().ShouldBe("#8B00FF");
    }

    // ── (f) typeColor from TypeAppearances fallback ────────────────────

    [Fact]
    public async Task TypeColor_FromTypeAppearancesFallback()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Epic", "Test", "Active"));

        var config = new TwigConfiguration
        {
            TypeAppearances = [new TypeAppearanceConfig { Name = "Epic", Color = "#FF00FF" }]
        };
        var writer = CreateWriter(config);
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("typeColor").GetString().ShouldBe("#FF00FF");
    }

    // ── (g) text field matches expected plain format ───────────────────

    [Fact]
    public async Task TextField_MatchesPlainFormat()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(42, "Bug", "Fix crash on login", "New", isDirty: true));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        var text = doc.RootElement.GetProperty("text").GetString();
        text.ShouldBe("✦ #42 Fix crash on login [New] •");
    }

    // ── (h) isDirty reflects work item state ──────────────────────────

    [Fact]
    public async Task IsDirty_True_WhenWorkItemDirty()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Task", "Do thing", "Active", isDirty: true));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("isDirty").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task IsDirty_False_WhenWorkItemClean()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Task", "Do thing", "Active", isDirty: false));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("isDirty").GetBoolean().ShouldBeFalse();
    }

    // ── (i) branch populated from .git/HEAD ───────────────────────────

    [Fact]
    public async Task Branch_PopulatedFromGitHead()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/login\n");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Task", "Do thing", "Active"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("branch").GetString().ShouldBe("feature/login");
    }

    // ── (j) branch is null when detached HEAD ─────────────────────────

    [Fact]
    public async Task Branch_NullWhenDetachedHead()
    {
        var gitDir = Path.Combine(_tempDir, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "abc1234def5678\n");

        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Task", "Do thing", "Active"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("branch").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── (k) atomic write — partial write does not corrupt file ────────

    [Fact]
    public async Task AtomicWrite_ExistingFileNotCorrupted_WhenNewWriteSucceeds()
    {
        // Write initial state
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Bug", "First item", "New"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var firstContent = File.ReadAllText(PromptJsonPath);
        firstContent.ShouldContain("\"id\": 1");

        // Write second state — verify file is valid
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Bug", "Updated item", "Active"));

        await writer.WritePromptStateAsync();

        var secondContent = File.ReadAllText(PromptJsonPath);
        secondContent.ShouldContain("\"title\": \"Updated item\"");
        secondContent.ShouldContain("\"state\": \"Active\"");

        // Verify the tmp file is cleaned up
        File.Exists(PromptJsonPath + ".tmp").ShouldBeFalse();
    }

    // ── (l) exception in writer does not propagate ────────────────────

    [Fact]
    public async Task Exception_DoesNotPropagate()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB not initialized"));

        var writer = CreateWriter();

        // Should not throw
        await Should.NotThrowAsync(async () => await writer.WritePromptStateAsync());
    }

    [Fact]
    public async Task Exception_FromWorkItemRepo_DoesNotPropagate()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB corrupt"));

        var writer = CreateWriter();
        await Should.NotThrowAsync(async () => await writer.WritePromptStateAsync());
    }

    // ── WritesEmptyObject when work item not found ────────────────────

    [Fact]
    public async Task WritesEmptyObject_WhenWorkItemNotFound()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        File.Exists(PromptJsonPath).ShouldBeTrue();
        File.ReadAllText(PromptJsonPath).ShouldBe("{}");
    }

    // ── stateCategory resolved correctly ──────────────────────────────

    [Fact]
    public async Task StateCategory_ResolvedFromProcessTypeStore()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "CustomType", "Custom item", "CustomDoing"));

        var processType = new ProcessTypeRecord
        {
            TypeName = "CustomType",
            States = [
                new StateEntry("CustomNew", StateCategory.Proposed, null),
                new StateEntry("CustomDoing", StateCategory.InProgress, null),
                new StateEntry("CustomDone", StateCategory.Completed, null),
            ]
        };
        _processTypeStore.GetByNameAsync("CustomType", Arg.Any<CancellationToken>()).Returns(processType);

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("stateCategory").GetString().ShouldBe("InProgress");
    }

    // ── generatedAt present and valid ISO 8601 ────────────────────────

    [Fact]
    public async Task GeneratedAt_IsPresentAndValid()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Task", "Test", "Active"));

        var before = DateTime.UtcNow;
        var writer = CreateWriter();
        await writer.WritePromptStateAsync();
        var after = DateTime.UtcNow;

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        var generatedAt = DateTimeOffset.Parse(doc.RootElement.GetProperty("generatedAt").GetString()!).UtcDateTime;
        generatedAt.ShouldBeGreaterThanOrEqualTo(before.AddSeconds(-1));
        generatedAt.ShouldBeLessThanOrEqualTo(after.AddSeconds(1));
    }

    // ── IProcessTypeStore exception falls through to heuristic ────────

    [Fact]
    public async Task ProcessTypeStoreException_FallsThroughToHeuristic()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Bug", "Some bug", "Active"));
        _processTypeStore.GetByNameAsync("Bug", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        var writer = CreateWriter();
        await Should.NotThrowAsync(async () => await writer.WritePromptStateAsync());

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        // Heuristic maps "Active" → InProgress
        doc.RootElement.GetProperty("stateCategory").GetString().ShouldBe("InProgress");
    }

    // ── Custom/unknown type uses first-char fallback for badge ────────

    [Fact]
    public async Task TypeBadge_CustomType_UsesFirstCharFallback()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "CustomType", "Custom item", "Active"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        doc.RootElement.GetProperty("typeBadge").GetString().ShouldBe("C");
    }

    // ── TruncateTitle unit tests ──────────────────────────────────────

    [Theory]
    [InlineData("Short title", 40, "Short title")]
    [InlineData("Exactly forty characters long string!!!!", 40, "Exactly forty characters long string!!!!")]
    [InlineData("This title is exactly forty-one chars long", 40, "This title is exactly forty-one chars l…")]
    [InlineData("", 40, "")]
    public void TruncateTitle_VariousInputs(string title, int maxWidth, string expected)
    {
        PromptStateWriter.TruncateTitle(title, maxWidth).ShouldBe(expected);
    }

    [Fact]
    public void TruncateTitle_ZeroMaxWidth_ReturnsEmpty()
    {
        PromptStateWriter.TruncateTitle("Hello", 0).ShouldBe(string.Empty);
    }

    [Fact]
    public void TruncateTitle_MaxWidthOne_ReturnsSingleEllipsis()
    {
        PromptStateWriter.TruncateTitle("Hello", 1).ShouldBe("…");
    }

    [Fact]
    public void TruncateTitle_NullTitle_ReturnsEmpty()
    {
        PromptStateWriter.TruncateTitle(null!, 40).ShouldBe(string.Empty);
    }

    // ── Integration: title truncated at 120 chars in written JSON ──────

    [Fact]
    public async Task WritePromptStateAsync_TruncatesLongTitle()
    {
        var longTitle = new string('A', 150);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _workItemRepo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(CreateWorkItem(1, "Bug", longTitle, "Active"));

        var writer = CreateWriter();
        await writer.WritePromptStateAsync();

        var doc = JsonDocument.Parse(File.ReadAllText(PromptJsonPath));
        var title = doc.RootElement.GetProperty("title").GetString()!;
        title.Length.ShouldBe(120);
        title.ShouldEndWith("…");
        title.ShouldBe(new string('A', 119) + "…");
    }
}
