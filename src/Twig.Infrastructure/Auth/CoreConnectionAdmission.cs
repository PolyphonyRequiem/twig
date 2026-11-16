using System.Text.Json;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;

namespace Twig.Infrastructure.Auth;

/// <summary>Read-only admission of the current Core store, never adoption of a marker or cache.</summary>
internal static class CoreConnectionAdmission
{
    internal static string ResolveDatabasePath(string twigDir)
        => File.Exists(Path.Combine(twigDir, CoreWorkspaceFactory.FileName))
            ? ReadDocument(twigDir).DatabasePath : Path.Combine(twigDir, "core.db");

    internal static void RequireCurrentDocument(TwigPaths paths) => _ = ReadDocument(paths.TwigDir);

    private static CoreWorkspaceDocument ReadDocument(string twigDir)
    {
        var marker = Path.Combine(twigDir, CoreWorkspaceFactory.FileName);
        if (!File.Exists(marker))
            throw new InvalidOperationException("activation-required: initialize the current Core workspace before work access.");
        using var file = File.OpenRead(marker);
        if (file.Length > 65536) throw new InvalidOperationException("activation-marker-invalid: current Core document exceeds its bound.");
        var document = JsonSerializer.Deserialize(file, CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument);
        if (document is not { Version: 1 } || document.DatabaseInstance == Guid.Empty
            || document.Connection is null || string.IsNullOrWhiteSpace(document.PrincipalId)
            || !Path.IsPathFullyQualified(document.DatabasePath)
            || !NativePathComparer.Instance.Equals(document.DatabasePath, Path.GetFullPath(document.DatabasePath)))
            throw new InvalidOperationException("activation-marker-invalid: an exact current Core store document is required.");
        return document;
    }

    internal static async Task<string> ValidateAsync(TwigPaths paths, string registryPath,
        ResolvedConnectionBinding origin, CancellationToken ct)
    {
        var document = ReadDocument(paths.TwigDir);
        if (document.Connection.Provider.Value != "ado"
            || document.Connection.IdentityRef != CleanCoreAdoAccess.Identity(origin, document.PrincipalId)
            || origin.Identity.AdoPrincipalId is { } principal && principal != document.PrincipalId
            || !NativePathComparer.Instance.Equals(Path.GetFullPath(paths.DbPath), document.DatabasePath)
            || !NativePathComparer.Instance.Equals(Path.GetFullPath(paths.RepoRoot), origin.WorktreeRoot))
            throw new InvalidOperationException("activation-authority-mismatch: the current Core store belongs to another checkout, selection or actor.");
        using var registry = new SqliteSystemWorktreeRegistry(registryPath, TimeProvider.System);
        var inventory = await registry.ReadCoreGateAsync(ct).ConfigureAwait(false);
        if (!inventory.IsSuccess)
            throw new InvalidOperationException("activation-registry-unavailable: " + inventory.Error.Message);
        var source = new NativeCoreStoreReference(document.DatabaseInstance, document.DatabasePath,
            origin.WorktreeRoot, document.Connection);
        if (!inventory.Value.Sources.Contains(source))
            throw new InvalidOperationException("activation-store-unregistered: the exact current Core store, root and connection are not registered.");
        var opened = await SqliteStore.OpenAsync(document.DatabasePath, ct, document.DatabaseInstance).ConfigureAwait(false);
        if (!opened.IsSuccess)
            throw new InvalidOperationException("activation-store-unavailable: " + opened.Error.Message);
        var pinned = await opened.Value.CheckWorkspaceAsync(document.Connection, ct).ConfigureAwait(false);
        if (!pinned.IsSuccess)
            throw new InvalidOperationException("activation-store-mismatch: " + pinned.Error.Message);
        if (ReadDocument(paths.TwigDir) != document)
            throw new InvalidOperationException("binding-changed: current Core store document changed during admission.");
        return "core:" + document.DatabaseInstance.ToString("N");
    }
}
