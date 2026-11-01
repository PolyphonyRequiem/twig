namespace Twig.Commands;

/// <summary>
/// Launches an external text editor for user input.
/// </summary>
public interface IEditorLauncher
{
    Task<string?> LaunchAsync(string initialContent, CancellationToken ct = default);
}
