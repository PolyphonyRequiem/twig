using System.Security.Cryptography;
using System.Text;
using Bench.Domain;
using Twig.Core;
using Twig.Infrastructure.Services;
using NativeBench = Twig.Core.Bench;

namespace Bench.Infrastructure;

/// <summary>Explicit, lossless migration only. The old database remains untouched and is not a production authority.</summary>
public static class LegacyBenchMigration
{
    public static async Task<Result<bool>> CheckAsync(string directory, UserBenchStore native, bool migrate, CancellationToken ct)
    {
        var database = Path.Combine(directory, "catalog.db");
        if (!File.Exists(database)) return Result<bool>.Ok(true);
        var opened = await SqliteCatalog.OpenAsync(database).ConfigureAwait(false);
        if (!opened.IsSuccess) return Result<bool>.Fail(opened.Error);
        var legacy = await opened.Value.ReadAsync(ct).ConfigureAwait(false);
        if (!legacy.IsSuccess) return Result<bool>.Fail(legacy.Error);
        var shared = await native.ReadAsync(ct).ConfigureAwait(false);
        if (!shared.IsSuccess) return Result<bool>.Fail(shared.Error);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            legacy.Value.Id.ToString("D") + "\n" + legacy.Value.Revision + "\n" + shared.Value.InstanceId.ToString("D")
            + "\n" + string.Join("\n", legacy.Value.Benches.OrderBy(bench => bench.Id).Select(CatalogFingerprint.Definition)))));
        var receipt = Path.Combine(directory, "native-migration.receipt");
        if (File.Exists(receipt))
            return await File.ReadAllTextAsync(receipt, ct).ConfigureAwait(false) == digest
                ? Result<bool>.Ok(true) : Result<bool>.Fail(ErrorCode.Conflict, "The preserved legacy definitions or native store changed. Reconcile migration explicitly.");
        if (!migrate) return Result<bool>.Fail(ErrorCode.Conflict, "Legacy panel definitions were preserved. Run once with --migrate-legacy to import them atomically into native Benches. Conflicts require explicit reconciliation.");
        var definitions = legacy.Value.Benches.Select(bench => new NativeBench(bench.Id, bench.Name,
            bench.Sources.SelectMany(source => source.Selectors.Select(selector => new Selector(selector.Kind,
                selector.Item, selector.Query, selector.HierarchyRelation, source.Binding.Connection))))).ToArray();
        var imported = await native.ImportAsync(definitions, ct).ConfigureAwait(false);
        if (!imported.IsSuccess) return Result<bool>.Fail(imported.Error);
        var temporary = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(digest), ct).ConfigureAwait(false);
                file.Flush(true);
            }
            File.Move(temporary, receipt, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return Result<bool>.Ok(true);
    }
}
