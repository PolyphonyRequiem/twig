using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// The <c>twig pending</c> adapter is a strict read-only projection. These tests pin the
/// two contracts the surface owns: exit code (always 0), and the machine shape — order
/// preserved, raw values verbatim, no coalescing.
/// </summary>
public sealed class PendingCommandTests
{
    private readonly IPendingChangeReader _reader = Substitute.For<IPendingChangeReader>();

    private PendingCommand CreateCommand(StringWriter stdout)
        => new(_reader, new RendererFactory(), stdout);

    [Fact]
    public async Task Execute_NoPendingChanges_ExitsZero_WithEmptyArray()
    {
        var stdout = new StringWriter();
        _reader.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingChangeDetail>());

        var cmd = CreateCommand(stdout);
        var exit = await cmd.ExecuteAsync(outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        var body = stdout.ToString();
        body.ShouldContain("\"count\": 0");
        body.ShouldContain("\"pendingChanges\": []");
    }

    [Fact]
    public async Task Execute_PreservesStagingOrder_AndRawValues()
    {
        var stdout = new StringWriter();
        _reader.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(new PendingChangeDetail[]
            {
                new(
                    PendingChangeId: 100,
                    WorkItemId: 42,
                    Kind: "batch",
                    Field: "System.State",
                    Note: null,
                    OldValue: "New",
                    NewValue: "  Active  ",
                    StagedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                    SeedRemap: null),
                new(
                    PendingChangeId: 42,
                    WorkItemId: 99,
                    Kind: "note",
                    Field: null,
                    Note: "hello <b>world</b>",
                    OldValue: null,
                    NewValue: "hello <b>world</b>",
                    StagedAt: DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
                    SeedRemap: null),
            });

        var cmd = CreateCommand(stdout);
        var exit = await cmd.ExecuteAsync(outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        using var document = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var pendingChanges = document.RootElement.GetProperty("pendingChanges");

        // Order is preserved exactly as returned by the reader (100 then 42).
        pendingChanges.GetArrayLength().ShouldBe(2);
        pendingChanges[0].GetProperty("pendingChangeId").GetInt64().ShouldBe(100);
        pendingChanges[1].GetProperty("pendingChangeId").GetInt64().ShouldBe(42);

        // Assert decoded JSON strings so default encoder escaping cannot obscure corruption.
        pendingChanges[0].GetProperty("newValue").GetString().ShouldBe("  Active  ");
        pendingChanges[1].GetProperty("note").GetString().ShouldBe("hello <b>world</b>");
        pendingChanges[1].GetProperty("newValue").GetString().ShouldBe("hello <b>world</b>");
    }

    [Fact]
    public async Task Execute_HumanOutput_ListsCount()
    {
        var stdout = new StringWriter();
        _reader.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(new PendingChangeDetail[]
            {
                new(
                    PendingChangeId: 1,
                    WorkItemId: 5,
                    Kind: "batch",
                    Field: "Priority",
                    Note: null,
                    OldValue: null,
                    NewValue: "1",
                    StagedAt: DateTimeOffset.UtcNow,
                    SeedRemap: null),
            });

        var cmd = CreateCommand(stdout);
        var exit = await cmd.ExecuteAsync(outputFormat: "human", ct: default);

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("1 pending change(s)");
    }

    [Fact]
    public async Task Execute_SeedRemapPresent_EmitsIdentityAndAlias()
    {
        var stdout = new StringWriter();
        var identity = StagedIdentity.New();
        StagedAlias.TryFrom(-3, out var alias).ShouldBeTrue();
        _reader.GetAllChangesAsync(Arg.Any<CancellationToken>())
            .Returns(new PendingChangeDetail[]
            {
                new(
                    PendingChangeId: 1,
                    WorkItemId: -3,
                    Kind: "batch",
                    Field: "System.Title",
                    Note: null,
                    OldValue: null,
                    NewValue: "seeded",
                    StagedAt: DateTimeOffset.UtcNow,
                    SeedRemap: new SeedRemapIdentity(identity, alias, PublishedWorkItemId: null)),
            });

        var cmd = CreateCommand(stdout);
        await cmd.ExecuteAsync(outputFormat: "json", ct: default);
        var body = stdout.ToString();
        body.ShouldContain("stagedIdentity");
        body.ShouldContain("stagedAlias");
        body.ShouldContain(identity.ToString());
        body.ShouldContain("\"stagedAlias\": -3");
    }
}
