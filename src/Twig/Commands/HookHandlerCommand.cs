using System.Text.RegularExpressions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Infrastructure.Config;

namespace Twig.Commands;

/// <summary>
/// Internal command invoked by git hook scripts: <c>twig _hook &lt;hook-name&gt; [args...]</c>.
/// Handles post-checkout (auto-detect context), prepare-commit-msg (prefix message),
/// and commit-msg (validate work item reference).
/// All output goes to stderr so it doesn't interfere with git's stdout processing.
/// </summary>
public sealed class HookHandlerCommand(
    IContextStore contextStore,
    IWorkItemRepository workItemRepo,
    TwigConfiguration config,
    IGitService? gitService = null)
{
    /// <summary>Handle a git hook invocation.</summary>
    public async Task<int> ExecuteAsync(string hookName, string[] args)
    {
        return hookName switch
        {
            "post-checkout" => await HandlePostCheckoutAsync(args),
            "prepare-commit-msg" => await HandlePrepareCommitMsgAsync(args),
            "commit-msg" => await HandleCommitMsgAsync(args),
            _ => 0, // Unknown hooks are silently ignored
        };
    }

    /// <summary>
    /// post-checkout: extract work item ID from new branch, set context.
    /// Args: [old-ref] [new-ref] [branch-flag]
    /// branch-flag = "1" means a branch switch (vs file checkout).
    /// </summary>
    private async Task<int> HandlePostCheckoutAsync(string[] args)
    {
        // args[2] is the branch flag: "1" = branch switch, "0" = file checkout
        if (args.Length < 3 || args[2] != "1")
            return 0;

        if (gitService is null)
            return 0;

        try
        {
            var branchName = await gitService.GetCurrentBranchAsync();
            var workItemId = WorkItemIdExtractor.Extract(branchName, config.Git.BranchPattern);
            if (workItemId is null)
                return 0;

            await contextStore.SetActiveWorkItemIdAsync(workItemId.Value);

            // Try to get work item title for the notification
            var item = await workItemRepo.GetByIdAsync(workItemId.Value);
            if (item is not null)
                Console.Error.WriteLine($"Twig context → #{workItemId.Value} ({item.Type.Value}: {item.Title})");
            else
                Console.Error.WriteLine($"Twig context → #{workItemId.Value}");
        }
        catch
        {
            // Hook failures must not break git operations
        }

        return 0;
    }

    /// <summary>
    /// prepare-commit-msg: prefix the commit message file with work item ID.
    /// Args: [commit-msg-file]
    /// </summary>
    private async Task<int> HandlePrepareCommitMsgAsync(string[] args)
    {
        if (args.Length < 1)
            return 0;

        var msgFile = args[0];

        try
        {
            var activeId = await contextStore.GetActiveWorkItemIdAsync();
            if (activeId is null)
                return 0;

            if (!File.Exists(msgFile))
                return 0;

            var content = await File.ReadAllTextAsync(msgFile);
            var prefix = $"#{activeId.Value} ";

            // Don't prefix if already contains the exact work item reference
            // Use non-digit lookahead to avoid false positives (e.g., #42 matching #420)
            if (Regex.IsMatch(content, $@"#{activeId.Value}(?!\d)"))
                return 0;

            await File.WriteAllTextAsync(msgFile, prefix + content);
        }
        catch
        {
            // Hook failures must not break git operations
        }

        return 0;
    }

    /// <summary>
    /// commit-msg: validate that the commit message contains a work item reference.
    /// Args: [commit-msg-file]
    /// </summary>
    private async Task<int> HandleCommitMsgAsync(string[] args)
    {
        if (args.Length < 1)
            return 0;

        var msgFile = args[0];

        try
        {
            var activeId = await contextStore.GetActiveWorkItemIdAsync();
            if (activeId is null)
                return 0;

            if (!File.Exists(msgFile))
                return 0;

            var content = await File.ReadAllTextAsync(msgFile);

            // Check if the message contains any work item reference (e.g., #42 or #12345)
            if (!Regex.IsMatch(content, @"#\d+"))
            {
                Console.Error.WriteLine($"Warning: commit message does not reference a work item (e.g., #{activeId.Value}).");
            }
        }
        catch
        {
            // Hook failures must not break git operations
        }

        return 0;
    }
}
