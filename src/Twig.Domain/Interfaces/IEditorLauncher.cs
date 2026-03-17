namespace Twig.Domain.Interfaces;

/// <summary>
/// Launches an external text editor for user input.
/// Domain interface; implementation deferred to Infrastructure (RD-011).
/// </summary>
public interface IEditorLauncher
{
    Task<string?> LaunchAsync(string initialContent, CancellationToken ct = default);
}
