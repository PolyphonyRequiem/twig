using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Encapsulates the File.Exists + File.ReadAllTextAsync + StatusFieldsConfig.Parse pattern
/// currently duplicated across StatusCommand, SetCommand, ShowCommand, and ConfigStatusFieldsCommand.
/// </summary>
public sealed class StatusFieldConfigReader(TwigPaths paths)
{
    public async Task<IReadOnlyList<StatusFieldEntry>?> ReadAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(paths.StatusFieldsPath))
            return null;
        try
        {
            var content = await File.ReadAllTextAsync(paths.StatusFieldsPath, ct);
            return StatusFieldsConfig.Parse(content);
        }
        catch { return null; }
    }
}
