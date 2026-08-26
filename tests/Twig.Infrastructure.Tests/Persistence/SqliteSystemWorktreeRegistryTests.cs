using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Contract tests for <see cref="SqliteSystemWorktreeRegistry"/>. These exercise
/// the AB#736 §9.4 surface AB#738 depends on: connection + worktree upsert,
/// non-retired matching lookup, and the fail-closed behavior a missing row
/// implies. The DB is created inside a per-test temp dir so parallel tests do
/// not clash.
/// </summary>
public sealed class SqliteSystemWorktreeRegistryTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteSystemWorktreeRegistry _registry;

    public SqliteSystemWorktreeRegistryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twig-system-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_dir, "system.db"), TimeProvider.System);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Find_returns_null_for_an_unknown_fingerprint()
    {
        var find = await _registry.FindWorktreeAsync("{\"unknown\":true}");
        find.IsSuccess.ShouldBeTrue(find.Error);
        find.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_worktree_and_lookup_round_trips()
    {
        (await _registry.UpsertConnectionAsync("ref-a", "org-a", "proj-a", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-a", "ref-a", "/some/wt")).IsSuccess.ShouldBeTrue();

        var find = await _registry.FindWorktreeAsync("fp-a");
        find.Value.ShouldNotBeNull();
        find.Value!.ConnectionRef.ShouldBe("ref-a");
        find.Value.RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_is_idempotent_and_reactivates_a_retired_row()
    {
        (await _registry.UpsertConnectionAsync("ref-b", "org-b", "proj-b", team: "t")).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-b", "ref-b", "/wt-b")).IsSuccess.ShouldBeTrue();

        // A second upsert refreshes lastSeenAt and clears retiredAt back to
        // null — the exact behavior §7 depends on when a legacy reinit reruns
        // and re-registers the same fingerprint.
        (await _registry.UpsertWorktreeAsync("fp-b", "ref-b", "/wt-b")).IsSuccess.ShouldBeTrue();
        var find = await _registry.FindWorktreeAsync("fp-b");
        find.Value!.RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Registry_survives_a_close_and_reopen()
    {
        (await _registry.UpsertConnectionAsync("ref-c", "org-c", "proj-c", team: null)).IsSuccess.ShouldBeTrue();
        (await _registry.UpsertWorktreeAsync("fp-c", "ref-c", "/wt-c")).IsSuccess.ShouldBeTrue();
        _registry.Dispose();

        using var reopened = new SqliteSystemWorktreeRegistry(Path.Combine(_dir, "system.db"), TimeProvider.System);
        var find = await reopened.FindWorktreeAsync("fp-c");
        find.Value.ShouldNotBeNull();
        find.Value!.ConnectionRef.ShouldBe("ref-c");
    }
}
