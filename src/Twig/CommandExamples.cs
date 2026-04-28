/// <summary>
/// Per-command usage examples appended to <c>--help</c> output after ConsoleAppFramework's
/// built-in help text. Handles compound commands (e.g. <c>nav up</c>, <c>seed new</c>) and
/// single-token commands (e.g. <c>set</c>, <c>show-batch</c>).
/// </summary>
internal static class CommandExamples
{
    /// <summary>
    /// Command name → array of example lines. Each line is a complete example string
    /// (e.g. <c>"twig set 1234              Set context by work item ID"</c>).
    /// Compound commands use space-separated keys (e.g. <c>"nav up"</c>).
    /// </summary>
    internal static Dictionary<string, string[]> Examples { get; } = new(StringComparer.Ordinal)
    {
        ["init"] =
        [
            "twig init https://dev.azure.com/myorg myproject    Initialize twig for a project",
            "twig init https://dev.azure.com/myorg myproject --pat <token>    Initialize with a PAT",
        ],
        ["set"] =
        [
            "twig set 1234              Set active context to work item #1234",
            "twig set login bug         Search cache for 'login bug' and set as active",
        ],
        ["show"] =
        [
            "twig show 1234             Show work item #1234",
            "twig show 1234 --output json  Show work item #1234 as JSON",
        ],
        ["show-batch"] =
        [
            "twig show-batch --batch 1234,5678,9012 --output json  Batch lookup as JSON array",
            "twig show-batch --batch 42             Single item batch lookup",
        ],
        ["query"] =
        [
            "twig query \"login bug\"              Search title & description",
            "twig query --state Doing --top 50    Filter by state, limit results",
        ],
        ["status"] =
        [
            "twig status                Show status of the active work item",
            "twig status --output json  Show status as JSON (for scripting)",
        ],
        ["state"] =
        [
            "twig state Active          Transition active item to Active",
            "twig state Done            Transition active item to Done",
        ],
        ["batch"] =
        [
            "twig batch --state Active                        Transition active item to Active",
            "twig batch --state Done --note \"Completed work\"  Transition and add a note",
            "twig batch --set Priority=1 --set Severity=High  Update multiple fields at once",
            "twig batch --state Active --ids 1234,5678        Batch transition multiple items",
        ],
        ["states"] =
        [
            "twig states                List available states for the active item's type",
            "twig states -o json        Output states as JSON (for automation/extensions)",
        ],
        ["new"] =
        [
            "twig new task \"Write tests\"         Create a new Task under the active item",
            "twig new bug \"Login fails on edge\"  Create a new Bug under the active item",
        ],
        ["tree"] =
        [
            "twig tree                  Render the sprint backlog as a tree",
            "twig tree --output json    Output the tree as JSON",
        ],
        ["nav"] =
        [
            "twig nav up                Navigate to the parent work item",
            "twig nav down              Navigate to the first child work item",
        ],
        ["nav up"] =
        [
            "twig nav up                Navigate to the parent work item",
            "twig nav up --output json  Navigate up and output result as JSON",
        ],
        ["nav down"] =
        [
            "twig nav down              Navigate to the first child of the active item",
            "twig nav down --output json  Navigate down and output as JSON",
        ],
        ["nav next"] =
        [
            "twig nav next              Navigate to the next sibling work item",
            "twig nav next --output json  Navigate to next sibling and output as JSON",
        ],
        ["nav prev"] =
        [
            "twig nav prev              Navigate to the previous sibling work item",
            "twig nav prev --output json  Navigate to previous sibling and output as JSON",
        ],
        ["nav back"] =
        [
            "twig nav back              Go back one step in navigation history",
            "twig nav back --output json  Go back and output result as JSON",
        ],
        ["nav fore"] =
        [
            "twig nav fore              Go forward one step in navigation history",
            "twig nav fore --output json  Go forward and output result as JSON",
        ],
        ["nav history"] =
        [
            "twig nav history           Show the navigation history stack",
            "twig nav history --output json  Output navigation history as JSON",
        ],
        ["web"] =
        [
            "twig web                   Open the active work item in a browser",
            "twig web 1234              Open work item #1234 in a browser",
        ],
        ["seed new"] =
        [
            "twig seed new              Start authoring a new seed work item",
            "twig seed new --type bug   Start authoring a new seed of type Bug",
        ],
        ["seed edit"] =
        [
            "twig seed edit             Edit the draft seed for the active item",
            "twig seed edit 1234        Edit the draft seed for item #1234",
        ],
        ["seed discard"] =
        [
            "twig seed discard          Discard the draft seed for the active item",
            "twig seed discard 1234     Discard the draft seed for item #1234",
        ],
        ["seed view"] =
        [
            "twig seed view             View the draft seed for the active item",
            "twig seed view --output json  View the seed as JSON",
        ],
        ["seed link"] =
        [
            "twig seed link 1234 5678   Link seed #1234 as a child of #5678",
            "twig seed link 1234        Link the seed #1234 under the active item",
        ],
        ["seed unlink"] =
        [
            "twig seed unlink 1234      Remove the parent link from seed #1234",
            "twig seed unlink           Remove the parent link from the active seed",
        ],
        ["seed links"] =
        [
            "twig seed links            List all seeds with their parent links",
            "twig seed links --output json  Output seed links as JSON",
        ],
        ["seed chain"] =
        [
            "twig seed chain            Show the full seed chain for the active item",
            "twig seed chain --output json  Output the seed chain as JSON",
        ],
        ["seed validate"] =
        [
            "twig seed validate         Validate all pending seeds before publish",
            "twig seed validate 1234    Validate seed #1234 specifically",
        ],
        ["seed publish"] =
        [
            "twig seed publish 42       Publish seed #42 to ADO",
            "twig seed publish --all    Publish all seeds in dependency order",
            "twig seed publish --all --link-branch feature/my-branch  Publish all and link to branch",
            "twig seed publish 42 --link-branch feature/my-branch     Publish and link to branch",
            "twig seed publish --dry-run  Preview what would be published",
        ],
        ["seed reconcile"] =
        [
            "twig seed reconcile        Reconcile local seeds against ADO state",
            "twig seed reconcile --output json  Output reconcile results as JSON",
        ],
        ["link parent"] =
        [
            "twig link parent 5678      Set #5678 as the parent of the active item",
            "twig link parent 5678 1234  Set #5678 as the parent of item #1234",
        ],
        ["link unparent"] =
        [
            "twig link unparent         Remove the parent link from the active item",
            "twig link unparent 1234    Remove the parent link from item #1234",
        ],
        ["link reparent"] =
        [
            "twig link reparent 5678        Move the active item under parent #5678",
            "twig link reparent 5678 1234   Move item #1234 under parent #5678",
        ],
        ["link artifact"] =
        [
            "twig link artifact https://example.com/doc --name \"Plan\"  Add a hyperlink",
            "twig link artifact vstfs:///Git/Commit/p/r/abc123         Add an artifact link",
            "twig link artifact https://example.com --id 42            Link to a specific item",
        ],
        ["link branch"] =
        [
            "twig link branch feature/my-branch       Link an existing branch to the active item",
            "twig link branch feature/my-branch --id 42  Link a branch to a specific item",
        ],
        ["note"] =
        [
            "twig note --text \"Investigated root cause\"   Add a note to the active item",
            "twig note                                    Open editor to compose a note",
        ],
        ["update"] =
        [
            "twig update System.Title \"New title\"           Update the title field",
            "twig update System.Description \"<p>…</p>\" --format markdown  Update description",
        ],
        ["edit"] =
        [
            "twig edit                  Open the active item's description in an editor",
            "twig edit System.Title     Open the Title field for editing",
        ],
        ["discard"] =
        [
            "twig discard               Discard all pending changes on the active item",
            "twig discard --yes         Skip the confirmation prompt",
        ],
        ["sync"] =
        [
            "twig sync                  Sync the working set from ADO",
            "twig sync --force          Force a full resync, bypassing the cache",
        ],
        ["workspace"] =
        [
            "twig workspace             Show the current workspace (cached items)",
            "twig workspace --output json  Output workspace info as JSON",
        ],
        ["workspace track"] =
        [
            "twig workspace track 1234          Pin work item #1234 to workspace",
            "twig workspace track 5678 -o json  Pin and output confirmation as JSON",
        ],
        ["workspace track-tree"] =
        [
            "twig workspace track-tree 1234     Pin #1234 and its subtree to workspace",
            "twig workspace track-tree 42       Pin an epic and all its children",
        ],
        ["workspace untrack"] =
        [
            "twig workspace untrack 1234        Stop tracking work item #1234",
            "twig workspace untrack 5678        Remove a pinned item from workspace",
        ],
        ["workspace exclude"] =
        [
            "twig workspace exclude 1234        Hide #1234 from workspace view",
            "twig workspace exclude 5678        Exclude a noisy item from sprint display",
        ],
        ["workspace exclusions"] =
        [
            "twig workspace exclusions              List all excluded work items",
            "twig workspace exclusions --clear      Remove all exclusions",
            "twig workspace exclusions --remove 42  Remove exclusion for #42",
            "twig workspace exclusions -o json      List exclusions as JSON",
        ],
        ["area"] =
        [
            "twig area                              Show area-filtered workspace view",
            "twig area -o json                      Output area view as JSON",
        ],
        ["area add"] =
        [
            "twig area add \"Project\\Team A\"           Add area path with subtree matching",
            "twig area add \"Project\\Team A\" --exact   Add area path with exact matching only",
        ],
        ["area remove"] =
        [
            "twig area remove \"Project\\Team A\"        Remove a configured area path",
            "twig area remove \"Project\\Team A\" -o json  Remove and output result as JSON",
        ],
        ["area list"] =
        [
            "twig area list                          List all configured area paths",
            "twig area list -o json                  List area paths as JSON",
        ],
        ["area sync"] =
        [
            "twig area sync                          Fetch team area paths from ADO",
            "twig area sync -o json                  Sync and output result as JSON",
        ],
        ["sprint"] =
        [
            "twig sprint                Show the current sprint summary",
            "twig sprint --output json  Output sprint info as JSON",
        ],
        ["config"] =
        [
            "twig config pat            Show the configured PAT setting",
            "twig config pat <token>    Set the PAT to a new value",
        ],
        ["config status-fields"] =
        [
            "twig config status-fields                    Show configured status fields",
            "twig config status-fields --output json      Output status fields as JSON",
        ],
        ["branch"] =
        [
            "twig branch                Create a branch name from the active work item",
            "twig branch --no-transition  Create branch without transitioning work item state",
        ],
        ["commit"] =
        [
            "twig commit                Create a commit message from the active work item",
            "twig commit \"fix: my message\"  Commit with a custom message",
        ],
        ["pr"] =
        [
            "twig pr                    Open or create a PR for the current branch",
            "twig pr --output json      Output PR info as JSON",
        ],
        ["stash"] =
        [
            "twig stash                 Stash pending changes on the active item",
            "twig stash \"WIP: my changes\"  Stash with a descriptive message",
        ],
        ["stash pop"] =
        [
            "twig stash pop             Restore the most recently stashed changes",
            "twig stash pop --output json  Output the restored stash details as JSON",
        ],
        ["log"] =
        [
            "twig log                   Show the change log for the active work item",
            "twig log --output json     Output the change log as JSON",
        ],
        ["context"] =
        [
            "twig context               Show the current twig context (workspace, active item)",
            "twig context --output json  Output context as JSON",
        ],
        ["version"] =
        [
            "twig version               Print the installed twig version",
            "twig version --output json  Output version info as JSON",
        ],
        ["upgrade"] =
        [
            "twig upgrade               Upgrade twig to the latest release",
            "twig upgrade               Run in a new shell after install to pick up PATH changes",
        ],
        ["changelog"] =
        [
            "twig changelog             Show the twig release changelog",
            "twig changelog --output json  Output changelog as JSON",
        ],
        ["tui"] =
        [
            "twig tui                   Launch the interactive terminal UI",
            "twig tui                   Navigate tree, status, and context interactively",
        ],
        ["mcp"] =
        [
            "twig mcp                   Start the twig MCP server",
            "twig mcp                   Exposes twig tools to AI agents via the MCP protocol",
        ],
        ["ohmyposh init"] =
        [
            "twig ohmyposh init         Output the oh-my-posh segment JSON for twig context",
            "twig ohmyposh init --output json  Same, explicitly requesting JSON output",
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
