using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig link branch &lt;branch-name&gt;</c>: links an existing git branch
/// to a work item as an ADO artifact link, without creating or checking it out.
/// Reuses <see cref="BranchLinkService"/> for the actual link creation.
/// </summary>
public sealed class LinkBranchCommand(
    ActiveItemResolver activeItemResolver,
    BranchLinkService? branchLinkService,
    OutputFormatterFactory formatterFactory,
    IGitService? gitService = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Link an existing branch to the active (or specified) work item.</summary>
    public async Task<int> ExecuteAsync(
        string branchName,
        int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (string.IsNullOrWhiteSpace(branchName))
        {
            _stderr.WriteLine(fmt.FormatError("Branch name is required."));
            return 1;
        }

        var resolved = id.HasValue
            ? await activeItemResolver.ResolveByIdAsync(id.Value, ct)
            : await activeItemResolver.GetActiveItemAsync(ct);

        if (!resolved.TryGetWorkItem(out var item, out var errorId, out _))
        {
            _stderr.WriteLine(fmt.FormatError(errorId is not null
                ? $"Work item #{errorId} not found in cache."
                : "No active work item. Run 'twig set <id>' or pass --id."));
            return 1;
        }

        var (isValid, exitCode) = await GitGuard.EnsureGitRepoAsync(gitService, fmt, _stderr);
        if (!isValid) return exitCode;

        if (branchLinkService is null)
        {
            _stderr.WriteLine(fmt.FormatError("Git project is not configured for this workspace. Set git.project and git.repository in config."));
            return 1;
        }

        bool branchExists;
        try
        {
            branchExists = await gitService!.BranchExistsAsync(branchName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _stderr.WriteLine(fmt.FormatError($"Failed to check branch existence: {ex.Message}"));
            return 1;
        }

        if (!branchExists)
        {
            _stderr.WriteLine(fmt.FormatError($"Branch '{branchName}' not found locally or on remote."));
            return 1;
        }

        var result = await branchLinkService.LinkBranchAsync(item.Id, branchName, ct);

        if (!result.IsSuccess)
        {
            _stderr.WriteLine(fmt.FormatError(
                $"Failed to link branch '{result.BranchName}' to #{result.WorkItemId}: {result.ErrorMessage}"));
            return 1;
        }

        if (string.Equals(outputFormat, "minimal", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(branchName);
        }
        else
        {
            var message = result.Status == BranchLinkStatus.AlreadyLinked
                ? $"Already linked: branch '{branchName}' on #{item.Id}."
                : $"Linked branch '{branchName}' to #{item.Id}.";
            Console.WriteLine(fmt.FormatSuccess(message));
        }
        return 0;
    }
}
