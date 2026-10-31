using Twig.Infrastructure.Services;

namespace Twig;

internal static class CoreMutationAdmissionGuard
{
    internal static string? Validate(IReadOnlyList<string> args, string twigDirectory)
    {
        if (args.Count == 0 || !RequiresCore(args)
            || File.Exists(Path.Combine(twigDirectory, CoreWorkspaceFactory.FileName)))
            return null;
        return "An explicitly initialized Core workspace is required for this mutation. "
            + "Existing legacy drafts, journals and creation fences were preserved; no legacy writer was opened. "
            + "Use a fresh isolated worktree and `twig init <org> <project> --core-store <new-absolute-file>`.";
    }

    private static bool RequiresCore(IReadOnlyList<string> args) => args[0] switch
    {
        "update" or "state" or "note" or "patch" or "edit" or "batch" or "delete" or "save" or "discard" or "new" or "link" => true,
        "sync" => !args.Contains("--pull-only"),
        "seed" => args.Count < 2 || args[1] is not ("view" or "validate" or "links"),
        "proposal" or "plan" => args.Count >= 2 && args[1] is "apply" or "reconcile" or "preview" or "validate",
        "draft" => args.Count >= 2 && args[1] is not ("show" or "review"),
        "bench" => args.Count >= 2 && args[1] is "browser" or "create" or "delete" or "switch"
            || args.Count >= 3 && args[1] == "discussion" && args[2] == "prepare-post",
        "tui" => true,
        "policy" => args.Count >= 2 && args[1] == "reconcile",
        _ => false,
    };
}
