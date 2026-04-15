/// <summary>
/// Per-command usage examples appended to <c>--help</c> output after ConsoleAppFramework's
/// built-in help text. Populated by T-1525-2; resolution logic handles compound commands
/// (e.g. <c>nav up</c>, <c>seed new</c>) and single-token commands (e.g. <c>set</c>, <c>flow-start</c>).
/// </summary>
internal static class CommandExamples
{
    /// <summary>
    /// Command name → array of example lines. Each line is a complete example string
    /// (e.g. <c>"twig set 1234              Set context by work item ID"</c>).
    /// Populated by T-1525-2; compound commands use space-separated keys (e.g. <c>"nav up"</c>).
    /// </summary>
    internal static Dictionary<string, string[]> Examples { get; } = new(StringComparer.Ordinal)
    {
        ["init"] =
        [
            "twig init myorg myproject                  Initialize workspace for org/project",
            "twig init myorg myproject --team MyTeam    Initialize with a specific team",
        ],
        ["set"] =
        [
            "twig set 1234                  Set active context to work item #1234",
            "twig set \"login bug\"           Set active context by title pattern",
        ],
        ["show"] =
        [
            "twig show 1234                 Show details of work item #1234",
            "twig show 1234 --output json   Show details in JSON format",
        ],
        ["query"] =
        [
            "twig query --state Active               Query all active work items",
            "twig query \"login\" --type Task          Search tasks matching 'login'",
        ],
        ["status"] =
        [
            "twig status                    Show current work item status",
            "twig status --output json      Show status in JSON format",
        ],
        ["state"] =
        [
            "twig state Active              Transition active item to Active",
            "twig state Done                Mark active item as Done",
        ],
        ["new"] =
        [
            "twig new \"Add login page\" --type Task     Create a new task",
            "twig new \"Fix crash\" --type Bug --set     Create a bug and set it active",
        ],
        ["tree"] =
        [
            "twig tree                      Show work item hierarchy for current context",
            "twig tree --depth 3            Show tree up to 3 levels deep",
        ],
        ["nav"] =
        [
            "twig nav                       Open interactive navigation picker",
            "twig nav --output minimal      Open picker with minimal output format",
        ],
        ["nav up"] =
        [
            "twig nav up                    Navigate to parent of active item",
            "twig nav up --output json      Navigate to parent, output JSON",
        ],
        ["nav down"] =
        [
            "twig nav down                  Navigate to first child of active item",
            "twig nav down \"fix crash\"      Navigate down to child matching pattern",
        ],
        ["nav next"] =
        [
            "twig nav next                  Navigate to next sibling of active item",
            "twig nav next --output minimal Navigate to next sibling, minimal output",
        ],
        ["nav prev"] =
        [
            "twig nav prev                  Navigate to previous sibling of active item",
            "twig nav prev --output json    Navigate to previous sibling, JSON output",
        ],
        ["nav back"] =
        [
            "twig nav back                  Go back in navigation history",
            "twig nav back --output minimal Go back, minimal output",
        ],
        ["nav fore"] =
        [
            "twig nav fore                  Go forward in navigation history",
            "twig nav fore --output minimal Go forward, minimal output",
        ],
        ["nav history"] =
        [
            "twig nav history               Show navigation history interactively",
            "twig nav history --non-interactive   Print history without prompting",
        ],
        ["web"] =
        [
            "twig web                       Open active work item in browser",
            "twig web 1234                  Open work item #1234 in browser",
        ],
        ["seed new"] =
        [
            "twig seed new \"Add login page\"         Create a new seed work item",
            "twig seed new --editor                  Create a seed and open editor",
        ],
        ["seed edit"] =
        [
            "twig seed edit 42              Edit seed work item with local ID 42",
            "twig seed edit 42 --output json   Edit seed and output result as JSON",
        ],
        ["seed discard"] =
        [
            "twig seed discard 42           Discard seed with local ID 42 (prompt)",
            "twig seed discard 42 --yes     Discard seed 42 without confirmation",
        ],
        ["seed view"] =
        [
            "twig seed view                 List all staged seed work items",
            "twig seed view --output json   List seeds in JSON format",
        ],
        ["seed link"] =
        [
            "twig seed link 42 1234                   Link seed 42 to item #1234",
            "twig seed link 42 1234 --type Implements  Link with a specific relation type",
        ],
        ["seed unlink"] =
        [
            "twig seed unlink 42 1234               Remove link between seed 42 and #1234",
            "twig seed unlink 42 1234 --type Tests  Remove a specific relation type link",
        ],
        ["seed links"] =
        [
            "twig seed links                List links for all seeds",
            "twig seed links 42             List links for seed with local ID 42",
        ],
        ["seed chain"] =
        [
            "twig seed chain \"Task 1\" \"Task 2\" \"Task 3\"   Create a chain of seeds",
            "twig seed chain --parent 1234 \"Task 1\" \"Task 2\"   Chain under parent #1234",
        ],
        ["seed validate"] =
        [
            "twig seed validate             Validate all staged seeds",
            "twig seed validate 42          Validate seed with local ID 42",
        ],
        ["seed publish"] =
        [
            "twig seed publish --all        Publish all validated seeds to ADO",
            "twig seed publish 42           Publish seed with local ID 42",
        ],
        ["seed reconcile"] =
        [
            "twig seed reconcile            Reconcile seeds against ADO work items",
            "twig seed reconcile --output json   Reconcile and output result as JSON",
        ],
        ["link parent"] =
        [
            "twig link parent 1234          Set item #1234 as parent of active item",
            "twig link parent 1234 --output json   Set parent, output JSON",
        ],
        ["link unparent"] =
        [
            "twig link unparent             Remove parent link from active item",
            "twig link unparent --output json    Remove parent, output JSON",
        ],
        ["link reparent"] =
        [
            "twig link reparent 1234        Move active item under parent #1234",
            "twig link reparent 1234 --output json   Reparent, output JSON",
        ],
        ["note"] =
        [
            "twig note \"Starting implementation\"    Add an inline note to active item",
            "twig note                               Open editor to compose a note",
        ],
        ["update"] =
        [
            "twig update System.Title \"New Title\"          Update the title field",
            "twig update System.Description \"Details\" --format markdown   Update with markdown",
        ],
        ["edit"] =
        [
            "twig edit                      Open active work item fields in editor",
            "twig edit --output json        Edit and output result as JSON",
        ],
        ["discard"] =
        [
            "twig discard                   Discard all pending local changes",
            "twig discard --output json     Discard changes, output JSON",
        ],
        ["sync"] =
        [
            "twig sync                      Sync active work item with ADO",
            "twig sync --force              Force a full sync, bypassing cache",
        ],
        ["workspace"] =
        [
            "twig workspace                 Show current sprint workspace",
            "twig workspace --all           Show all work items in sprint layout",
        ],
        ["sprint"] =
        [
            "twig sprint                    Show sprint board view",
            "twig sprint --output json      Show sprint board as JSON",
        ],
        ["config"] =
        [
            "twig config sprint.columns \"State,Title\"   Set a config value",
            "twig config sprint.columns                  Read the current value",
        ],
        ["config status-fields"] =
        [
            "twig config status-fields                   Show configured status fields",
            "twig config status-fields --output json     Show status fields as JSON",
        ],
        ["branch"] =
        [
            "twig branch                    Create a git branch for active work item",
            "twig branch --no-transition    Create branch without state transition",
        ],
        ["commit"] =
        [
            "twig commit \"Fix null ref\"     Commit with work item link in message",
            "twig commit --no-link          Commit without auto-linking work item",
        ],
        ["pr"] =
        [
            "twig pr                        Create a pull request for active item",
            "twig pr --draft                Create a draft pull request",
        ],
        ["stash"] =
        [
            "twig stash                     Stash pending changes on active item",
            "twig stash \"WIP: half done\"    Stash with a descriptive message",
        ],
        ["stash pop"] =
        [
            "twig stash pop                 Restore the most recent stash",
            "twig stash pop --output json   Restore stash, output JSON",
        ],
        ["log"] =
        [
            "twig log                       Show recent activity log (last 20 entries)",
            "twig log --count 50            Show last 50 log entries",
        ],
        ["flow-start"] =
        [
            "twig flow-start 1234           Start working on item #1234 end-to-end",
            "twig flow-start \"login bug\" --no-branch   Start without creating a branch",
        ],
        ["flow-done"] =
        [
            "twig flow-done                 Save changes, push, and create a PR",
            "twig flow-done --no-pr         Save and push without creating a PR",
        ],
        ["flow-close"] =
        [
            "twig flow-close                Close active flow and clean up branch",
            "twig flow-close --force        Force close even with uncommitted changes",
        ],
        ["hooks install"] =
        [
            "twig hooks install             Install git hooks into .git/hooks",
            "twig hooks install --output json   Install hooks, output JSON",
        ],
        ["hooks uninstall"] =
        [
            "twig hooks uninstall           Remove twig git hooks from .git/hooks",
            "twig hooks uninstall --output json   Uninstall hooks, output JSON",
        ],
        ["context"] =
        [
            "twig context                   Show active workspace and work item context",
            "twig context --output json     Show context in JSON format",
        ],
        ["version"] =
        [
            "twig version                   Print the installed twig version",
            "twig version --output json     Print version in JSON format",
        ],
        ["upgrade"] =
        [
            "twig upgrade                   Upgrade twig to the latest release",
            "twig upgrade --output json     Upgrade and output result as JSON",
        ],
        ["changelog"] =
        [
            "twig changelog                 Show last 5 twig release notes",
            "twig changelog --count 10      Show last 10 release notes",
        ],
        ["tui"] =
        [
            "twig tui                       Launch the terminal UI",
            "twig tui --output json         Launch TUI with JSON output mode",
        ],
        ["mcp"] =
        [
            "twig mcp                       Start the MCP server",
            "twig mcp --output json         Start MCP server with JSON output",
        ],
        ["ohmyposh init"] =
        [
            "twig ohmyposh init             Generate Oh My Posh segment config",
            "twig ohmyposh init --shell zsh Generate config for zsh shell",
        ],
    };

    /// <summary>
    /// Resolves the command name from <paramref name="args"/> and prints usage examples
    /// if any are registered. When <c>args.Length &gt;= 2</c>, tries the compound key
    /// <c>"{args[0]} {args[1]}"</c> first (e.g. <c>"nav up"</c>), then falls back to
    /// <c>args[0]</c> (e.g. <c>"set"</c>). Does nothing if no examples match.
    /// </summary>
    internal static void ShowIfPresent(string[] args)
    {
        if (args.Length == 0) return;

        var compound = args.Length >= 2 ? $"{args[0]} {args[1]}" : null;
        var key = compound is not null && Examples.ContainsKey(compound) ? compound : args[0];

        if (!Examples.TryGetValue(key, out var examples))
            return;

        Console.WriteLine();
        Console.WriteLine("Examples:");
        foreach (var example in examples)
            Console.WriteLine($"  {example}");
    }
}
