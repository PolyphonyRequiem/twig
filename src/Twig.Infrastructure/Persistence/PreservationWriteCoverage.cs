namespace Twig.Infrastructure.Persistence;

internal sealed record PreservationWritePath(string Surface, string Disposition);

internal static class PreservationWriteCoverage
{
    internal const int Version = 1;

    internal static IReadOnlyList<PreservationWritePath> Paths { get; } =
    [
        new("work-item.save-and-batch", "seed-dual-write-provider-mirror-unchanged"),
        new("work-item.delete-evict-reparent-clear-dirty", "seed-dual-write"),
        new("context.set-clear", "dual-write"),
        new("navigation.record-prune-trim-back-forward-clear", "dual-write-with-sequence"),
        new("pending.edit-note-add-clear-seed-filter", "existing-durable-authority"),
        new("seed.identity-links-intent-published-map", "existing-durable-authority"),
        new("proposal.journal-receipts-drafts", "existing-durable-authority"),
        new("local.fields-identity-relationships-tombstones", "existing-durable-authority"),
        new("provider.links-verification-process-fields-calendar-sync", "existing-mirror-authority"),
        new("connection.identity-generation-change", "blocked-before-native-intent-and-reset"),
        new("connection.default-generation-change", "blocked-before-native-family-intent-and-reset"),
        new("connection.legacy-import", "already-admitted-no-op"),
        new("mirror.schema-rebuild", "blocked-before-destructive-ddl"),
        new("preservation.intent-cancel-activate-rollback", "durable-cas-with-retained-history-and-backups"),
    ];

    internal static void RequireComplete()
    {
        if (Version != 1 || Paths.Count != 14
            || Paths.Any(path => string.IsNullOrWhiteSpace(path.Disposition))
            || Paths.Select(path => path.Surface).Distinct(StringComparer.Ordinal).Count() != Paths.Count)
            throw new InvalidOperationException("preservation-write-coverage-unverified: cutover and rollback require the complete reviewed write-path matrix.");
    }
}
