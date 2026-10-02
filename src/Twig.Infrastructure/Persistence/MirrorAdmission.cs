using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Persistence;

internal sealed record MirrorAdmissionDocument(int Version, string RegistryPath, string Fingerprint,
    string ConnectionRef, string BindingId, string Generation, string MirrorFile);

/// <summary>The marker routes storage; the native registry, not a marker or caller assertion, admits it.</summary>
internal sealed class MirrorAdmission
{
    internal const string MarkerFile = "admission.json";
    private readonly string _twigDir;
    private readonly MirrorAdmissionDocument _document;

    private MirrorAdmission(string twigDir, MirrorAdmissionDocument document)
    {
        _twigDir = twigDir;
        _document = document;
    }

    internal string Generation => _document.Generation;
    internal string WorktreeRoot => Path.GetDirectoryName(_twigDir)!;

    internal IDisposable AcquireOperation()
    {
        var lease = Auth.ConnectionOperationGate.Acquire(WorktreeRoot);
        try { Validate(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    internal string MirrorPath => Path.Combine(_twigDir, "cache", _document.MirrorFile);

    internal static string ResolveMirrorPath(string twigDir)
    {
        var marker = Path.Combine(twigDir, "cache", MarkerFile);
        if (!File.Exists(marker)) return Path.Combine(twigDir, "cache", "twig.db");
        var document = ReadDocument(marker);
        ValidateDocument(document);
        return Path.Combine(twigDir, "cache", document.MirrorFile);
    }

    internal static MirrorAdmission? Acquire(TwigPaths paths, string? registryPath = null)
    {
        var marker = Path.Combine(paths.TwigDir, "cache", MarkerFile);
        if (!File.Exists(marker))
        {
            // Only a real native intent needs the expensive fresh Git fingerprint check.
            // Absence is not host-quiescence evidence and never activates migration.
            var registry = registryPath ?? Path.Combine(Auth.ConnectionBindingService.ResolveUserHome(), "system.db");
            if (!File.Exists(registry) || !HasMigrationIntents(registry)) return null;
            if (!WorktreeAnchorDetector.TryDetect(paths.StartDir, out var anchor, out _))
                throw new InvalidOperationException("migration-fingerprint-unavailable: native migration intent exists but checkout identity cannot be verified; restore the worktree before access.");
            if (ReadNativeState(registry, WorktreeFingerprintProvider.CanonicalJson(anchor)) is not null)
                throw new InvalidOperationException("migration-incomplete: resume 'twig connection migrate' with the original identity; no legacy or partial-generation access is admitted.");
            return null;
        }
        var document = ReadDocument(marker);
        ValidateDocument(document);
        var expectedRegistry = registryPath ?? Path.Combine(Auth.ConnectionBindingService.ResolveUserHome(), "system.db");
        if (!SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(document.RegistryPath), Path.GetFullPath(expectedRegistry)))
            throw new InvalidOperationException("migration-authority-mismatch: the mirror belongs to another metadata home. Reconnect using the original explicit binding authority; no cache or work request is admitted.");
        if (!SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(paths.DbPath), Path.GetFullPath(Path.Combine(paths.TwigDir, "cache", document.MirrorFile))))
            throw new InvalidOperationException("legacy-store-retired: reconnect using freshly resolved workspace paths.");
        if (!WorktreeAnchorDetector.TryDetect(paths.StartDir, out var current, out _)
            || document.Fingerprint != WorktreeFingerprintProvider.CanonicalJson(current))
            throw new InvalidOperationException("migration-fingerprint-mismatch: the admitted store belongs to another checkout.");
        var admission = new MirrorAdmission(paths.TwigDir, document);
        admission.Validate();
        return admission;
    }

    internal void Validate()
    {
        var current = ReadDocument(Path.Combine(_twigDir, "cache", MarkerFile));
        if (current != _document)
            throw new InvalidOperationException("binding-changed: storage admission changed. Explicitly reconnect; the old generation is unavailable.");
        ValidateNoUnfinishedTransition(_document.RegistryPath, _document.Fingerprint);
        if (!WorktreeAnchorDetector.TryDetect(WorktreeRoot, out var anchor, out _)
            || WorktreeFingerprintProvider.CanonicalJson(anchor) != _document.Fingerprint)
            throw new InvalidOperationException("binding-changed: checkout fingerprint changed. Explicitly reconnect before reading or writing cached data.");
        var paths = TwigPaths.BuildPaths(_twigDir, new TwigConfiguration(), WorktreeRoot);
        if (ConnectionRefResolver.Compute(TwigConfiguration.LoadSplit(paths)) != _document.ConnectionRef)
            throw new InvalidOperationException("binding-changed: declared repository endpoint changed. Explicitly reconnect; cached data remains owned by its original endpoint.");
        var native = ReadNativeState(_document.RegistryPath, _document.Fingerprint);
        if (native is null || native.Version != 1 || native.State != "active"
            || native.Generation != _document.Generation || native.ConnectionRef != _document.ConnectionRef
            || native.BindingId != _document.BindingId || !SqlitePlanJournalRepository.CreateDefaultSourcePathComparer().Equals(Path.GetFullPath(native.CurrentMirror), Path.GetFullPath(MirrorPath)))
            throw new InvalidOperationException("migration-incomplete: native activation is not complete for this mirror generation. Resume the original migration; no cached/read or work HTTP access is admitted.");
    }

    private static bool HasMigrationIntents(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='connection_migrations';";
        if (command.ExecuteScalar() is null) return false;
        command.CommandText = "SELECT 1 FROM connection_migrations LIMIT 1;";
        return command.ExecuteScalar() is not null;
    }

    internal static ConnectionMigrationRecord? ReadNativeState(string database, string fingerprint)
    {
        ValidateNoUnfinishedTransition(database, fingerprint);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='connection_migrations';";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return null;
        command.CommandText = "SELECT state, generation, record_json FROM connection_migrations WHERE worktree_fingerprint = $fp;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var record = JsonSerializer.Deserialize(reader.GetString(2), TwigJsonContext.Default.ConnectionMigrationRecord)
            ?? throw new InvalidOperationException("migration-intent-unreadable: native record is invalid.");
        if (record.State != reader.GetString(0) || record.Generation != reader.GetString(1))
            throw new InvalidOperationException("migration-intent-inconsistent: native admission metadata disagrees; restore its native record before proceeding.");
        reader.Dispose();
        var attachmentPath = Path.Combine(record.WorktreeRoot, ".twig", "attachment.json");
        var attachment = JsonSerializer.Deserialize(File.ReadAllText(attachmentPath), TwigJsonContext.Default.AttachmentDocument)
            ?? throw new InvalidOperationException("binding-changed: the admitted attachment cannot be verified. Restore it, then reconnect explicitly.");
        if (attachment.Schema != AttachmentDocument.CurrentSchema || attachment.Version != AttachmentDocument.CurrentVersion
            || attachment.Revision < 0 || (attachment.BindingPin is not null && string.IsNullOrWhiteSpace(attachment.BindingPin)))
            throw new InvalidOperationException("binding-changed: checkout attachment version or binding pin cannot be verified. Repair the attachment, then reconnect explicitly.");
        if (attachment.ConnectionRef != record.ConnectionRef)
            throw new InvalidOperationException("binding-changed: checkout endpoint no longer matches its native generation. Reconnect explicitly.");
        var selected = attachment.BindingPin;
        if (selected is null)
        {
            command.CommandText = "SELECT binding_id FROM connection_defaults WHERE connection_ref=$connection;";
            command.Parameters.AddWithValue("$connection", record.ConnectionRef);
            selected = command.ExecuteScalar() as string;
        }
        if (!string.Equals(selected, record.BindingId, StringComparison.Ordinal))
            throw new InvalidOperationException("binding-changed: the native mirror generation no longer owns effective pin/default selection. Explicitly reconnect after completing its guarded transition.");
        return record;
    }

    internal static void ValidateNoUnfinishedTransition(string database, string fingerprint)
    {
        if (!File.Exists(database)) return;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='connection_binding_transitions';";
        if (command.ExecuteScalar() is null) return;
        command.CommandText = "SELECT 1 FROM connection_binding_transitions WHERE worktree_fingerprint=$fp AND state <> 'completed' LIMIT 1;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        if (command.ExecuteScalar() is not null)
            throw new InvalidOperationException("binding-transition-incomplete: this checkout is fenced by its unfinished native binding intent. Resume the original connection pin/unpin with its confirmed digest, then explicitly reconnect; no read, cache fill or work HTTP is admitted.");
    }

    internal static MirrorAdmissionDocument ReadDocument(string path)
        => JsonSerializer.Deserialize(File.ReadAllText(path), TwigJsonContext.Default.MirrorAdmissionDocument)
            ?? throw new InvalidOperationException("migration-marker-unreadable: restore the native migration record; no fallback is admitted.");

    private static void ValidateDocument(MirrorAdmissionDocument document)
    {
        if (document.Version != 1 || !Guid.TryParseExact(document.Generation, "N", out _)
            || document.MirrorFile.Length != 44
            || !document.MirrorFile.StartsWith("admitted-", StringComparison.Ordinal)
            || !document.MirrorFile.EndsWith(".db", StringComparison.Ordinal)
            || !Guid.TryParseExact(document.MirrorFile.AsSpan(9, document.MirrorFile.Length - 12), "N", out _)
            || !Path.IsPathFullyQualified(document.RegistryPath))
            throw new InvalidOperationException("migration-version-unsupported: unsupported storage admission. Use the compatible Twig version; do not reset or delete durable data.");
    }
}
