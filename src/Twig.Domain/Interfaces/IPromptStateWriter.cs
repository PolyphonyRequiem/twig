namespace Twig.Domain.Interfaces;

/// <summary>
/// Writes the pre-computed prompt state file (<c>.twig/prompt.json</c>).
/// Called by mutating commands after their primary operation completes.
/// Implementations MUST swallow all exceptions — prompt state writes must never fail the parent command.
/// </summary>
public interface IPromptStateWriter
{
    void WritePromptState();
}
