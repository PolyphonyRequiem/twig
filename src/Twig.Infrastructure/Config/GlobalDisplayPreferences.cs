using System.Text.Json;
using Twig.Infrastructure.Serialization;

namespace Twig.Infrastructure.Config;

/// <summary>Machine-wide display defaults; never contains a workspace connection or credentials.</summary>
public sealed class GlobalDisplayPreferences
{
    public string? Icons { get; set; }

    public static GlobalDisplayPreferences Load(string path)
    {
        if (!File.Exists(path)) return new GlobalDisplayPreferences();
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), TwigJsonContext.Default.GlobalDisplayPreferences)
                ?? new GlobalDisplayPreferences();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new TwigConfigurationException($"Cannot read global display config '{path}': {ex.Message}", ex);
        }
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(this, TwigJsonContext.Default.GlobalDisplayPreferences);
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
