using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Attachment;
using Twig.Domain.Services.Claims;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// AB#739 durable per-tuple operation epoch. Every mint/reclaim/release
/// reserves a monotonic epoch before its remote projection, then
/// CAS-commits the epoch with the winning claimId + casToken. A later
/// reserver forces a losing commit to fail, which drives the converge
/// loop in <see cref="Twig.Infrastructure.Services.Claims.LocalClaimService"/>.
/// </summary>
public sealed class SqliteSystemWorktreeRegistryEpochTests : IDisposable
{
    private const string Kind = PrimaryScopeKinds.AdoWorkItem;
    private readonly string _dir;
    private readonly SqliteSystemWorktreeRegistry _registry;

    public SqliteSystemWorktreeRegistryEpochTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "twig-epoch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _registry = new SqliteSystemWorktreeRegistry(Path.Combine(_dir, "system.db"), TimeProvider.System);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task SeedAsync()
    {
        (await _registry.UpsertConnectionAsync("ref-e", "org", "proj", team: null)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reserve_returns_monotonic_epoch()
    {
        await SeedAsync();
        var a = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 100);
        var b = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 100);
        var c = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 100);
        a.IsSuccess.ShouldBeTrue();
        b.IsSuccess.ShouldBeTrue();
        c.IsSuccess.ShouldBeTrue();
        b.Value.ShouldBeGreaterThan(a.Value);
        c.Value.ShouldBeGreaterThan(b.Value);
    }

    [Fact]
    public async Task Reserve_scopes_epoch_by_tuple()
    {
        await SeedAsync();
        var a100 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 100);
        var b101 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 101);
        var c100 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 100);
        a100.Value.ShouldBe(1);
        b101.Value.ShouldBe(1); // independent counter per tuple
        c100.Value.ShouldBe(2);
    }

    [Fact]
    public async Task Commit_records_winner_when_expected_epoch_matches()
    {
        await SeedAsync();
        var e = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 200);
        var commit = await _registry.CommitTupleEpochAsync("ref-e", Kind, 200, e.Value, "CLM-A", "cas-1");
        commit.IsSuccess.ShouldBeTrue();

        var row = await _registry.GetTupleEpochAsync("ref-e", Kind, 200);
        row.IsSuccess.ShouldBeTrue();
        row.Value.Epoch.ShouldBe(e.Value);
        row.Value.WinningClaimId.ShouldBe("CLM-A");
        row.Value.WinningCasToken.ShouldBe("cas-1");
    }

    [Fact]
    public async Task Commit_fails_when_a_later_reserver_raised_the_epoch()
    {
        await SeedAsync();
        var e1 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 300);
        // A second reserver races in and raises the epoch.
        await _registry.ReserveTupleEpochAsync("ref-e", Kind, 300);

        // e1's commit at its reserved epoch must fail.
        var commit = await _registry.CommitTupleEpochAsync("ref-e", Kind, 300, e1.Value, "CLM-A", "cas-1");
        commit.IsSuccess.ShouldBeFalse();
        commit.Error.ShouldBe(AttachmentStorageFailure.ClaimTupleEpochMismatch);

        // Row shows the raised epoch but no winner yet.
        var row = await _registry.GetTupleEpochAsync("ref-e", Kind, 300);
        row.Value.Epoch.ShouldBeGreaterThan(e1.Value);
        row.Value.WinningClaimId.ShouldBeNull();
    }

    [Fact]
    public async Task Get_returns_zero_row_for_unknown_tuple()
    {
        await SeedAsync();
        var row = await _registry.GetTupleEpochAsync("ref-e", Kind, 9999);
        row.IsSuccess.ShouldBeTrue();
        row.Value.Epoch.ShouldBe(0);
        row.Value.WinningClaimId.ShouldBeNull();
        row.Value.WinningCasToken.ShouldBeNull();
    }

    //
    // Two lifecycle operations reserve overlapping epochs. The atomic
    // transition+epoch commit ensures the losing operation's claim row
    // is rolled back — the tuple never carries an active non-winner
    // row.

    [Fact]
    public async Task Atomic_activate_rolls_back_when_epoch_moved_between_reserve_and_commit()
    {
        await SeedAsync();
        (await _registry.UpsertWorktreeAsync("fp-e", "ref-e", "/wt")).IsSuccess.ShouldBeTrue();

        // Insert pending row for claim X (epoch e1 reserved by X).
        (await _registry.InsertClaimAsync("CLM-X", "ref-e", "fp-e", Kind, 555, "pending", "cas-X", "{}")).IsSuccess.ShouldBeTrue();
        var e1 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 555);
        e1.IsSuccess.ShouldBeTrue();

        // A concurrent operation reserves e2 (higher).
        var e2 = await _registry.ReserveTupleEpochAsync("ref-e", Kind, 555);
        e2.Value.ShouldBeGreaterThan(e1.Value);

        // X attempts atomic activate+epoch commit at e1: MUST fail on epoch mismatch.
        var atomic = await _registry.ActivateClaimAndCommitEpochAsync(
            "CLM-X", "cas-X", "cas-X-new", DateTimeOffset.UtcNow, "{}",
            "ref-e", Kind, 555, e1.Value);
        atomic.IsSuccess.ShouldBeFalse();
        atomic.Error.ShouldBe(AttachmentStorageFailure.ClaimTupleEpochMismatch);

        // Row was rolled back — still pending, NOT active.
        var find = await _registry.FindClaimAsync("CLM-X");
        find.Value.ShouldNotBeNull();
        find.Value!.State.ShouldBe("pending");
        find.Value.CasToken.ShouldBe("cas-X"); // unchanged

        // Epoch row has no winner.
        var row = await _registry.GetTupleEpochAsync("ref-e", Kind, 555);
        row.Value.WinningClaimId.ShouldBeNull();
    }
}
