using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Infrastructure.Services;

/// <summary>Recorded native identity evidence, not a credential or an authorization capability.</summary>
public sealed record NativeSourceStamp(Connection Connection, Guid StoreInstance, string PrincipalId, string AuthorityStamp);

/// <summary>A coherent local snapshot and, when requested, separately observed provider schema.</summary>
public sealed record NativeSourceObservation(NativeSourceStamp Source, StoreSnapshot Snapshot,
    DateTimeOffset CacheReadAt, Result<ProcessSchema>? Schema, DateTimeOffset? SchemaObservedAt);

/// <summary>Request-scoped reads of an explicitly initialized native workspace. No native mutations are exposed.</summary>
public static class NativeSourceReader
{
    /// <summary>
    /// Reads recorded native data without token acquisition or HTTP unless schema observation is requested.
    /// An expected source refuses identity/store replacement, not ordinary snapshot version changes.
    /// Calls have a thirty-second ceiling; caller cancellation propagates. Schema failures refuse all content.
    /// Offline permission, previously observed schema and known-denial policy belong to the calling host.
    /// </summary>
    public static Task<Result<NativeSourceObservation>> ReadAsync(string workspaceRoot,
        NativeSourceStamp? expectedSource = null, bool observeSchema = false, CancellationToken ct = default)
        => ReadAsync(workspaceRoot, expectedSource, observeSchema, null, ct);

    internal static async Task<Result<NativeSourceObservation>> ReadAsync(string workspaceRoot,
        NativeSourceStamp? expectedSource, bool observeSchema, Action<IServiceCollection>? configure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        try
        {
            if (string.IsNullOrWhiteSpace(workspaceRoot) || !Path.IsPathFullyQualified(workspaceRoot)
                || !NativePathComparer.Instance.Equals(workspaceRoot, Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot))))
                return Result<NativeSourceObservation>.Fail(ErrorCode.InvalidInput, "An explicit canonical workspace root is required.");
            var twig = Path.Combine(workspaceRoot, ".twig");
            if (!Directory.Exists(workspaceRoot) || !File.Exists(Path.Combine(twig, CoreWorkspaceFactory.FileName)))
                return Result<NativeSourceObservation>.Fail(ErrorCode.StorageUnavailable,
                    "An existing explicitly initialized Core workspace is required; nothing was initialized.");

            // Acquire here, before any await, so nested native operations inherit reentrancy.
            using var operation = ConnectionOperationGate.Acquire(workspaceRoot, exclusive: true);
            using var runtime = await ComposeAsync().ConfigureAwait(false);
            var factory = runtime.GetRequiredService<CoreWorkspaceFactory>();
            var opened = await factory.OpenAsync(token).ConfigureAwait(false);
            if (!opened.IsSuccess) return Refuse(opened.Error, "Native workspace admission failed.");
            using var handle = opened.Value;
            var admitted = await factory.AdmitSourceAsync(handle, token).ConfigureAwait(false);
            if (!admitted.IsSuccess) return Refuse(admitted.Error, "Native authority admission failed.");
            if (expectedSource is not null && expectedSource != handle.Source)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict,
                    "The recorded source authority or store changed; explicit source reauthorization is required.");
            var snapshot = await handle.Workspace.SnapshotAsync(token).ConfigureAwait(false);
            if (!snapshot.IsSuccess) return Refuse(snapshot.Error, "Native snapshot acquisition failed.");
            var readAt = DateTimeOffset.UtcNow;
            Result<ProcessSchema>? schema = null;
            DateTimeOffset? schemaAt = null;
            if (observeSchema)
            {
                schema = await handle.Workspace.SchemaAsync(token).ConfigureAwait(false);
                if (!schema.IsSuccess) return Refuse(schema.Error, "Native schema observation failed; no offline fallback was selected.");
                schemaAt = DateTimeOffset.UtcNow;
            }

            using var finalRuntime = await ComposeAsync().ConfigureAwait(false);
            var finalFactory = finalRuntime.GetRequiredService<CoreWorkspaceFactory>();
            var reopened = await finalFactory.OpenAsync(token).ConfigureAwait(false);
            if (!reopened.IsSuccess) return Refuse(reopened.Error, "Native workspace revalidation failed.");
            using var final = reopened.Value;
            var revalidated = await finalFactory.AdmitSourceAsync(final, token).ConfigureAwait(false);
            if (!revalidated.IsSuccess) return Refuse(revalidated.Error, "Native authority revalidation failed.");
            if (handle.Source != final.Source)
                return Result<NativeSourceObservation>.Fail(ErrorCode.Conflict,
                    "Native authority, workspace location or store changed during capture; no content was released.");
            token.ThrowIfCancellationRequested();
            return Result<NativeSourceObservation>.Ok(new(handle.Source, snapshot.Value, readAt, schema, schemaAt));

            async Task<ServiceProvider> ComposeAsync()
            {
                var paths = TwigPaths.BuildPaths(twig, new TwigConfiguration(), workspaceRoot);
                var configuration = await TwigConfiguration.LoadSplitAsync(paths, token).ConfigureAwait(false);
                var services = new ServiceCollection();
                configure?.Invoke(services);
                services.AddCoreWorkspaceServices(configuration, paths);
                return services.BuildServiceProvider();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "The native source read exceeded its request deadline.");
        }
        catch (ConnectionBindingResolutionException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable, "Native binding admission is unavailable or another operation is active.");
        }
        catch (SqliteException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.StorageUnavailable,
                "Native admission storage is unavailable; no content was released.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or TwigConfigurationException)
        {
            return Result<NativeSourceObservation>.Fail(ErrorCode.Unavailable,
                "The native source could not be admitted or revalidated; no content was released.");
        }
    }

    internal static NativeSourceStamp Stamp(CoreWorkspaceDocument document, string root, params string?[] authority)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        writer.WriteStringValue(Path.GetFullPath(root));
        writer.WriteStringValue(Path.GetFullPath(document.DatabasePath));
        foreach (var value in authority) writer.WriteStringValue(value);
        writer.WriteEndArray();
        writer.Flush();
        return new(document.Connection, document.DatabaseInstance, document.PrincipalId,
            Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray())));
    }

    internal static NativeSourceStamp Stamp(CoreWorkspaceDocument document, string root, ResolvedConnectionBinding origin)
        => Stamp(document, root, origin.Binding.BindingId, origin.Binding.ConnectionRef, Number(origin.Binding.Revision),
            origin.Identity.IdentityId, origin.Identity.TenantId, origin.Identity.ObjectId, origin.Identity.Issuer,
            origin.Identity.AuthorityHost, origin.Identity.CredentialRef, origin.Identity.Method,
            origin.Identity.AdoPrincipalId, origin.Identity.AdoAuthority, origin.WorktreeRoot,
            origin.SelectionSource, Number(origin.SelectionRevision), origin.StorageGeneration,
            origin.Operation.Organization, origin.Operation.Project, origin.Operation.Team,
            origin.Operation.WorktreeFingerprint, Number(origin.Operation.AttachmentRevision));

    internal static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Result<NativeSourceObservation> Refuse(Error error, string message)
        => Result<NativeSourceObservation>.Fail(new Error(error.Code, message)
        {
            Evidence = error.Evidence.Where(value => value is "http:401" or "http:403").ToImmutableArray(),
        });
}
