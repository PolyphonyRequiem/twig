namespace Twig.Infrastructure.Persistence;

internal sealed record ConnectionActivationRecord(
    int Version, string Fingerprint, string WorktreeRoot, string ConnectionRef,
    string Generation, string State, string IdentityName, string BindingId,
    string CurrentMirror, long AttachmentRevision);

internal sealed partial class SqliteSystemWorktreeRegistry
{
    private const string ActivationSchemaSql = """
        CREATE TABLE connection_activations (
            worktree_fingerprint TEXT PRIMARY KEY,
            generation TEXT NOT NULL UNIQUE,
            state TEXT NOT NULL CHECK (state IN ('preparing', 'active')),
            record_json TEXT NOT NULL
        );
        """;
}
