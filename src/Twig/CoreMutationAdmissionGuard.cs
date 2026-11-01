using Twig.Infrastructure.Services;

namespace Twig;

internal static class CoreMutationAdmissionGuard
{
    internal static string? Validate(IReadOnlyList<string> args, string twigDirectory)
    {
        if (args.Contains("--help") || args.Contains("-h")) return null;
        if (args.Count == 0) return null;
        if (ValidateCatalog(args) is { } retired) return retired;
        if (NativeHost(args)) return null;
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
        return "An explicitly initialized Core workspace is required for Core data commands. "
            + "Existing legacy drafts, journals and creation fences were preserved; no legacy writer was opened. "
            + "Use a fresh isolated worktree and `twig init <org> <project> --core-store <new-absolute-file>`.";
    }

    internal static string? ValidateCatalog(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Contains("--help") || args.Contains("-h") || !Retired(args)) return null;
        var command = args[0];
        if (args.Count > 1 && !args[1].StartsWith('-')
            && args[0] is "auth" or "connection" or "bench" or "workspace" or "process"
                or "proposal" or "plan" or "seed" or "draft" or "nav" or "link")
            command += " " + args[1];
        return $"Unsupported: {command} or its UI envelope is retired from current delivery; no provider, credential or legacy workflow was opened.";
    }

    private static bool NativeHost(IReadOnlyList<string> args) => args[0] switch
    {
        "init" or "skills" or "help" or "version" => true,
        "auth" => args.Count > 1 && args[1] is "login" or "pat" or "identities" or "status",
        "connection" => args.Count > 1 && args[1] is "bind" or "list" or "status" or "writes",
        _ => false,
    };

    private static bool Retired(IReadOnlyList<string> args)
    {
        if (args.Any(value => value.Split('=', 2)[0] is "--browser" or "--include-browser" or "--browser-version"
            or "--expect-binding" or "--expect-identity" or "--expect-settings" or "--include-management"
            or "--include-rendering" or "--interactive" or "--live" or "--all-backlog"))
            return true;
        return args[0] switch
        {
            "init" or "skills" or "help" or "version" => false,
            "auth" or "connection" => !NativeHost(args),
            "show" or "show-batch" or "query" or "update" or "patch" or "state" or "states" or "note"
                or "batch" or "pending" or "save" or "discard" or "set" or "refresh" or "sync" or "edit" => false,
            "tree" => args.Contains("--all"),
            "nav" => args.Count < 2 || args[1] is not ("up" or "down" or "next" or "prev"),
            "process" => args.Count > 1 && args[1] == "layout",
            "seed" => args.Count < 2 || args[1] is not ("new" or "view" or "validate" or "publish" or "discard" or "reconcile" or "chain" or "edit" or "link" or "unlink" or "links"),
            "proposal" or "plan" => args.Count < 2 || args[1] is not ("validate" or "preview" or "apply" or "status"),
            "bench" => args.Count > 1 && !args[1].StartsWith('-') && args[1] is not ("list" or "create" or "switch"),
            "workspace" or "ws" => args.Contains("--all") || args.Contains("--tree")
                || args.Count > 1 && !args[1].StartsWith('-') && args[1] is not ("view" or "track" or "track-tree" or "untrack" or "sync"),
            "link" => false,
            _ => true,
        };
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

    private static bool RequiresCore(IReadOnlyList<string> args) => !NativeHost(args);
}
