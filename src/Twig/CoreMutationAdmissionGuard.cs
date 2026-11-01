using Twig.Infrastructure.Services;

namespace Twig;

internal static class CoreMutationAdmissionGuard
{
    internal static string? Validate(IReadOnlyList<string> args, string twigDirectory)
    {
        if (args.Contains("--help") || args.Contains("-h")) return null;
        var marker = Path.Combine(twigDirectory, CoreWorkspaceFactory.FileName);
        if (File.Exists(marker) && args.Count != 0)
        {
            try
            {
                var workspace = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(marker),
                    CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument);
                if (workspace?.Connection.Provider.Value == "github" && !GitHubQualified(args))
                    return "This command family is not qualified for the native GitHub Core CLI profile; no provider HTTP or legacy writer was used.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return "The pinned Core workspace marker is unavailable or invalid; no command fallback was used.";
            }
        }
        if (args.Count == 0 || !RequiresCore(args)
            || File.Exists(Path.Combine(twigDirectory, CoreWorkspaceFactory.FileName)))
            return null;
        return "An explicitly initialized Core workspace is required for this mutation. "
            + "Existing legacy drafts, journals and creation fences were preserved; no legacy writer was opened. "
            + "Use a fresh isolated worktree and `twig init <org> <project> --core-store <new-absolute-file>`.";
    }

    private static bool GitHubQualified(IReadOnlyList<string> args) => args[0] switch
    {
        "show" => !args.Contains("--tree"),
        "show-batch" or "process" or "update" or "note" or "state" or "batch" or "patch"
            or "save" or "discard" or "pending" or "query" => !args.Contains("--browser"),
        "link" => args.Count > 2 && args[1] == "local" && args[2] is "related" or "unrelate" or "list",
        "bench" => args.Count == 1 || args.Count > 1 && args[1] is "list" or "create" or "switch",
        "workspace" or "ws" => args.Count == 1 || args[1].StartsWith('-') || args[1] is "view" or "track" or "untrack",
        "seed" => args.Count > 1 && args[1] is "new" or "view" or "publish" or "discard" or "validate",
        "proposal" or "plan" => args.Count > 1 && args[1] is "preview" or "apply" or "status" or "validate",
        "skills" or "help" or "version" => true,
        _ => false,
    };

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
