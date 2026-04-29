namespace Twig.Infrastructure.Config;

/// <summary>
/// Thrown when a command requires a twig workspace but none was found in
/// the directory tree. Distinct from cache corruption — this indicates
/// the workspace has never been initialized in this directory.
/// </summary>
public sealed class WorkspaceNotFoundException : InvalidOperationException
{
    private const string DefaultMessage =
        "No twig workspace found. Run 'twig init --org <org> --project <project>' to create one.";

    public WorkspaceNotFoundException() : base(DefaultMessage) { }

    public WorkspaceNotFoundException(string message) : base(message) { }

    public WorkspaceNotFoundException(string message, Exception innerException)
        : base(message, innerException) { }
}
