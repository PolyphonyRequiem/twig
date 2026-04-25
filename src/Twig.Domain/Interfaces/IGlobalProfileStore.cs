using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// Reads and writes global process profiles stored at
/// <c>~/.twig/profiles/{org}/{process}/</c>.
/// All operations are best-effort — load methods return null on any failure,
/// and save methods silently swallow I/O exceptions.
/// </summary>
public interface IGlobalProfileStore
{
    Task<string?> LoadStatusFieldsAsync(string org, string process, CancellationToken ct = default);
    Task SaveStatusFieldsAsync(string org, string process, string content, CancellationToken ct = default);
    Task<ProfileMetadata?> LoadMetadataAsync(string org, string process, CancellationToken ct = default);
    Task SaveMetadataAsync(string org, string process, ProfileMetadata metadata, CancellationToken ct = default);
}
