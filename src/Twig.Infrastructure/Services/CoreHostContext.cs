using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Core;
using Twig.Infrastructure.Auth;

namespace Twig.Infrastructure.Services;

internal sealed record CoreContextKey(Guid? LocalId, string? NativeId)
{
    internal static CoreContextKey Capture(ItemKey key) => key.IsLocal ? new(key.LocalId, null) : new(null, key.NativeId);
    internal ItemKey Resolve(Connection connection)
    {
        if (LocalId is { } local && local != Guid.Empty && NativeId is null) return ItemKey.Local(local);
        if (LocalId is null && !string.IsNullOrWhiteSpace(NativeId)) return ItemKey.Remote(connection.Provider, connection.ResourceScope, NativeId);
        throw new JsonException("The retained active/history identity is invalid.");
    }
}

internal sealed record CoreContextDocument(int Version, Connection Connection, Guid StoreInstance,
    CoreContextKey[] History, int Position, Guid? BenchId = null);

internal sealed class CoreHostContext(string workspaceRoot, Connection connection, Guid storeInstance)
{
    private string PathName => Path.Combine(workspaceRoot, ".twig", "core-context.json");

    internal async Task<Result<CoreContextDocument>> ReadAsync(CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(workspaceRoot, exclusive: false);
        if (storeInstance == Guid.Empty) return Result<CoreContextDocument>.Fail(ErrorCode.StorageUnavailable, "Core context requires the admitted store instance.");
        if (!File.Exists(PathName)) return Result<CoreContextDocument>.Ok(new(1, connection, storeInstance, [], -1));
        try
        {
            await using var file = File.OpenRead(PathName);
            var document = await JsonSerializer.DeserializeAsync(file, CoreContextJsonContext.Default.CoreContextDocument, ct).ConfigureAwait(false);
            if (document is not { Version: 1 } || document.Connection != connection || document.StoreInstance != storeInstance
                || document.History is null || document.Position < -1 || document.Position >= document.History.Length
                || document.History.Length != 0 && document.Position == -1)
                return Result<CoreContextDocument>.Fail(ErrorCode.Conflict, "The retained active/history context belongs to a different store or is incompatible; it was preserved.");
            foreach (var key in document.History)
            {
                if (key is null) throw new JsonException("The retained active/history identity is missing.");
                _ = key.Resolve(connection);
            }
            return Result<CoreContextDocument>.Ok(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return Result<CoreContextDocument>.Fail(ErrorCode.StorageUnavailable, $"The retained active/history context could not be read; it was preserved: {ex.Message}");
        }
    }

    internal async Task<Result<bool>> WriteAsync(CoreContextDocument expected, CoreContextDocument next, CancellationToken ct)
    {
        using var operation = ConnectionOperationGate.Acquire(workspaceRoot, exclusive: true);
        var current = await ReadAsync(ct).ConfigureAwait(false);
        if (!current.IsSuccess) return Result<bool>.Fail(current.Error);
        if (current.Value.Connection != expected.Connection || current.Value.StoreInstance != expected.StoreInstance
            || current.Value.Position != expected.Position || current.Value.BenchId != expected.BenchId
            || !current.Value.History.SequenceEqual(expected.History))
            return Result<bool>.Fail(ErrorCode.Conflict, "Active/history context changed during selection; the newer selection was preserved.");
        if (next.Connection != connection || next.StoreInstance != storeInstance)
            return Result<bool>.Fail(ErrorCode.Conflict, "The selected context is outside the admitted workspace.");
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, next, CoreContextJsonContext.Default.CoreContextDocument, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, PathName, overwrite: true);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<bool>.Fail(ErrorCode.StorageUnavailable, $"Active/history context was not saved: {ex.Message}");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CoreContextDocument))]
internal partial class CoreContextJsonContext : JsonSerializerContext;
