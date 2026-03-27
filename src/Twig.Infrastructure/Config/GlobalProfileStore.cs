using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>
/// File-backed implementation of <see cref="IGlobalProfileStore"/>.
/// All operations are best-effort: loads return null on any failure,
/// saves silently swallow I/O exceptions (FR-09).
/// <see cref="OperationCanceledException"/> is always re-thrown to honour
/// the standard .NET cooperative cancellation contract.
/// </summary>
public sealed class GlobalProfileStore : IGlobalProfileStore
{
    private readonly string? _basePath;

    public GlobalProfileStore() { }

    /// <summary>
    /// Creates a store rooted at <paramref name="basePath"/> instead of the user's home directory.
    /// Used by tests to isolate file I/O to a temp directory.
    /// </summary>
    internal GlobalProfileStore(string basePath) => _basePath = basePath;

    public async Task<string?> LoadStatusFieldsAsync(string org, string process, CancellationToken ct = default)
    {
        try
        {
            var path = GetStatusFieldsPath(org, process);
            if (!File.Exists(path))
                return null;

            return await File.ReadAllTextAsync(path, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public async Task SaveStatusFieldsAsync(string org, string process, string content, CancellationToken ct = default)
    {
        var path = GetStatusFieldsPath(org, process);
        var tmpPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await File.WriteAllTextAsync(tmpPath, content, ct);
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(tmpPath); } catch { /* ignore cleanup failure */ }
            throw;
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* ignore cleanup failure */ }
        }
    }

    public async Task<ProfileMetadata?> LoadMetadataAsync(string org, string process, CancellationToken ct = default)
    {
        try
        {
            var path = GetMetadataPath(org, process);
            if (!File.Exists(path))
                return null;

            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(stream, TwigJsonContext.Default.ProfileMetadata, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    public async Task SaveMetadataAsync(string org, string process, ProfileMetadata metadata, CancellationToken ct = default)
    {
        var path = GetMetadataPath(org, process);
        var tmpPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            await using (var stream = File.Create(tmpPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, TwigJsonContext.Default.ProfileMetadata, ct);
            }
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(tmpPath); } catch { /* ignore cleanup failure */ }
            throw;
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* ignore cleanup failure */ }
        }
    }

    private string GetStatusFieldsPath(string org, string process)
        => _basePath is null
            ? GlobalProfilePaths.GetStatusFieldsPath(org, process)
            : Path.Combine(_basePath, TwigPaths.SanitizePathSegment(org),
                TwigPaths.SanitizePathSegment(process), "status-fields");

    private string GetMetadataPath(string org, string process)
        => _basePath is null
            ? GlobalProfilePaths.GetMetadataPath(org, process)
            : Path.Combine(_basePath, TwigPaths.SanitizePathSegment(org),
                TwigPaths.SanitizePathSegment(process), "profile.json");
}
