using System.Text;
using Twig.Core;
using PlanModel = Twig.Core.Core;

/// <summary>
/// Per-command usage examples appended to <c>--help</c> output after ConsoleAppFramework's
/// built-in help text. Handles compound commands (e.g. <c>nav up</c>, <c>seed new</c>) and
/// single-token commands (e.g. <c>set</c>, <c>show-batch</c>).
/// </summary>
internal static class CommandExamples
{
    /// <summary>
    /// Command name → array of example lines. Each line is a complete example string
    /// (e.g. <c>"twig show 1234             Show work item #1234"</c>).
    /// Compound commands use space-separated keys (e.g. <c>"bench list"</c>).
    /// </summary>
    internal static Dictionary<string, string[]> Examples { get; } = new(StringComparer.Ordinal)
    {
        ["bench rules"] =
        [
            "twig bench rules \"My Bench\" -o json     Read a named shared definition without fetching items",
        ],
        ["bench rule add"] =
        [
            "twig bench rule add \"My Bench\" --area \"Project\\Area\" --iteration \"@Current\" --mine   One AND rule, OR with independent Bench pins",
            "twig bench rule add \"My Bench\" --query <native-query-guid> --expect-revision 3 -o json   Pin identity/team, not today's result IDs",
        ],
        ["bench rule remove"] =
        [
            "twig bench rule remove \"My Bench\" --index 2 --expect-revision 3           Remove exactly the captured rule",
            "twig bench rule remove <bench-guid> --index 1 --expect-revision 7 -o json   No upstream work item or local draft deletion",
        ],
        ["bench planning"] =
        [
            "twig bench planning                      Read cached areas and the selected team's iteration calendar",
            "twig bench planning -o json              No remote refresh or changes to Bench intent",
        ],
        ["bench planning show"] =
        [
            "twig bench planning show                 Read the cached native planning metadata",
            "twig bench planning show -o json         Structured cached metadata, not an item sync",
        ],
        ["bench planning refresh"] =
        [
            "twig bench planning refresh              Explicit native metadata read; pins and items are unchanged",
            "twig bench planning refresh -o json      Refresh exact-team calendar before relative iteration configuration",
        ],
        ["bench sources"] =
        [
            "twig bench sources                       Discover actual native saved queries and exact-team backlogs",
            "twig bench sources -o json               Read IDs, hierarchy and availability without executing membership",
        ],
        ["bench browse"] =
        [
            "twig bench browse --query <native-query-guid> -o json   Explicitly acquire native membership and item bodies",
            "twig bench browse --backlog <native-backlog-id> -o ids  Find specific items without pinning or changing Bench selection",
        ],
        ["auth logout"] =
        [
            "twig auth logout --provider github --identity personal  Remove selected local app-user tokens, not remote authorization",
            "twig auth logout --provider github --identity personal --client-id <registered-app-client-id> -o json  Explicit app selection",
        ],
        ["draft save"] =
        [
            "twig draft save --stdin -o json  Consume the exact retained capture from the human workbench Save action",
            "twig draft review <retained-digest> -o json  Inspect the capture without applying it",
        ],
        ["bench discussion"] =
        [
            "twig bench discussion --stdin -o json       Read one page from an explicit qualified item/Bench JSON request through EOF",
            "twig bench discussion --stdin -o minimal    Same admission; pass the returned opaque cursor in a subsequent request for another page",
        ],
        ["bench discussion prepare-post"] =
        [
            "twig bench discussion prepare-post --stdin          Prepare native plan bytes from qualified context, format and text JSON through EOF; never posts",
            "twig bench discussion prepare-post --stdin -o json  Return the exact native body and canonical digest for separate plan review and authorization",
        ],
        ["bench attachments"] =
        [
            "twig bench attachments --stdin -o json       Read the explicit qualified Bench attachment inventory",
            "twig bench attachments --stdin --framed       Write one bounded binary frame for an explicit download request",
        ],
        ["init"] =
        [
            // Onboarding is four distinct operations, in order: enroll an identity once,
            // bind the endpoint to it, initialize the checkout against that binding, then
            // verify access. `init` only performs the third step — it requires an identity
            // already enrolled and bound (no account fallback). Verification uses
            // `process --refresh`, a real read-only ADO metadata request, because
            // `connection status` only inspects LOCAL saved state and never contacts ADO.
            "twig auth login --identity work                                        1) enroll an identity, once",
            "twig connection add --provider ado --org contoso --project Fabrikam     2) register the project",
            "twig connection bind --provider ado --org contoso --project Fabrikam --identity work   3) bind the enrolled identity",
            "twig init contoso Fabrikam                                             4) create a fresh native Core store at .twig/core.db",
            "twig init contoso Fabrikam --binding <id>                               Pin a registered binding for this fresh checkout only",
            "twig init contoso Fabrikam --core-store C:\\isolated\\core.db             Initialize a fresh isolated ADO checkout with a new explicitly bound Core store",
            "twig init --provider github --host github.com --repository owner/repo --account me   Create .twig/core.db using the exact selected native gh account",
            "twig init --provider twig --database personal                          Create or select the named native database; keep a separate workspace client store",
            "twig init --provider twig --database team-work -o json                  Share this owner's named database across explicitly initialized repositories",
            "twig init --org contoso --project Fabrikam --team \"Team A\"             Initialize with an explicit team",
            "twig process --refresh                                                 5) verify remote metadata access without work-item sync",
            "twig connection status                                                 optional: inspect the local binding (no ADO call)",
        ],
        ["show"] =
        [
            "twig show 1234             Show work item #1234",
            "twig show 1234 --output json  Show work item #1234 as JSON",
            "twig show --id 1234 -o json  Explicit machine target; never reads the active item",
            "twig show 1234 -o json --fields System.Title,System.State  Read selected fields with provenance",
            "twig show --local-id 0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b -o json  Read by canonical local identity",
            "twig show --local-id 0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b -o json-compact  Same JSON shape via alias",
            "twig show 1234 --tree      Show tree hierarchy rooted at #1234",
        ],
        ["tree-set"] =
        [
            "twig tree-set --items 101,102,103    Render three items as a forest of trees",
            "twig tree-set --items @ids.txt --annotate @notes.json    Annotate each node from a JSON map",
            "twig tree-set --items 101,102 --output json    Emit the tree structure with annotations for a TUI",
            "twig tree-set --items 101,102 --roots-only     Skip connecting ancestors",
        ],
        ["show-batch"] =
        [
            "twig show-batch 1234,5678,9012         Batch lookup work items",
            "twig show-batch --batch 1234,5678,9012 --output json  Batch lookup as JSON array",
            "twig show-batch --batch 42             Single item batch lookup",
            "twig show-batch 1234,5678 -o json --fields System.State  Read selected fields, disclosing misses",
            "twig show-batch --batch 42,-1 --include-fields -o json  Export complete stored targets only",
        ],
        ["query"] =
        [
            "twig query \"login bug\"              Search title & description",
            "twig query --state Doing --top 50    Filter by state, limit results",
        ],
        ["state"] =
        [
            "twig state Doing --id 42  Transition item #42 to a discovered state",
        ],
        ["batch"] =
        [
            "twig batch --state Doing --id 42  Stage a transition for one item",
            "twig batch --set Priority=1 --ids 42,43  Update an explicit set of items",
        ],
        ["process"] =
        [
            "twig process               List all work item types with state counts",
            "twig process Task          Show states, fields, and transitions for Task type",
            "twig process Bug -o json   Show Bug type details as JSON",
            "twig process --refresh     Recover process and field metadata without syncing work items",
            "twig process description <type-ref> -o json       Inspect the pinned provider's declared requirements",
        ],
        ["process layout"] =
        [
            "twig process layout Bug                      Show the Bug form's tabs, boxes, and fields",
            "twig process layout Bug --out layout.txt     Write the readable layout to a file",
            "twig process layout Bug -o json --out l.json  Write the machine-readable layout to a file",
            "twig process layout Bug --org otherorg --project otherproj   Read another project's Bug form layout",
        ],
        ["process description"] =
        [
            "twig process description                            Summarise every type in this project's process",
            "twig process description -o json --out proc.json    Write the complete document, for diffing",
            "twig process description Niflheim.Grilling          Describe one type, by its reference name",
            "twig process description Niflheim.Grilling -o json --sections requirements  Read scoped requirements",
        ],
        ["new"] =
        [
            "twig new task \"Write tests\"         Create a new Task under the active item",
            "twig new bug \"Login fails on edge\"  Create a new Bug under the active item",
        ],
        ["web"] =
        [
            "twig web                   Open the active work item in a browser",
            "twig web 1234              Open work item #1234 in a browser",
        ],
        ["seed new"] =
        [
            "twig seed new --no-parent --type Task --title \"Write tests\"  Create a local draft",
            "twig seed new --parent 42 --type Task --title \"Follow up\"  Create a child draft",
        ],
        ["seed edit"] =
        [
            "twig seed edit -42  Edit a specific local draft",
        ],
        ["seed discard"] =
        [
            "twig seed discard -42 --yes  Discard a specific local draft",
        ],
        ["seed view"] =
        [
            "twig seed view -o json  List retained seed drafts",
        ],
        ["seed link"] =
        [
            "twig seed link -42 5678 --type parent-child  Link seed #-42 as a child of #5678",
            "twig seed link -42 -43 --type blocked-by     Make seed #-42 depend on seed #-43",
        ],
        ["seed unlink"] =
        [
            "twig seed unlink -42 5678 --type parent-child  Remove seed #-42's parent link to #5678",
            "twig seed unlink -42 -43 --type blocked-by     Remove the dependency between two seeds",
        ],
        ["seed links"] =
        [
            "twig seed links            List all seeds with their parent links",
            "twig seed links --output json  Output seed links as JSON",
        ],
        ["seed chain"] =
        [
            "twig seed chain \"Design,Build\" --parent 42 --type Task  Create a chain of local drafts",
        ],
        ["seed validate"] =
        [
            "twig seed validate         Validate all pending seeds before publish",
            "twig seed validate -42    Validate seed #1234 specifically",
        ],
        ["seed publish"] =
        [
            "twig seed publish -42       Publish seed #42 to ADO",
            "twig seed publish --all    Publish all seeds in dependency order",
            "twig seed publish --all --link-branch feature/my-branch  Publish all and link to branch",
            "twig seed publish -42 --link-branch feature/my-branch     Publish and link to branch",
            "twig seed publish --all --dry-run  Preview what would be published",
        ],
        ["seed reconcile"] =
        [
            "twig seed reconcile        Reconcile local seeds against ADO state",
            "twig seed reconcile --output json  Output reconcile results as JSON",
        ],
        ["link parent"] =
        [
            "twig link parent 5678 --id 42      Set #5678 as the parent of item #42",
            "twig link parent 5678 1234  Set #5678 as the parent of item #1234",
            "twig link parent 5678 --id 1234 -o json  Apply and verify an explicit child",
            "twig link parent 5678 --id 1234 --child 1234 -o json  Cross-check the mandatory machine --id",
        ],
        ["link unparent"] =
        [
            "twig link unparent --id 42         Remove the parent link from item #42",
            "twig link unparent 1234    Remove the parent link from item #1234",
            "twig link unparent --id 1234 -o json  Remove and verify the observed parent",
        ],
        ["link reparent"] =
        [
            "twig link reparent 5678 --id 42        Unsupported: refuses without removing the current parent",
            "twig link reparent 5678 --id 1234 -o json  Report the unsupported replacement as JSON",
        ],
        ["link predecessor"] =
        [
            "twig link predecessor 65 --id 42        Mark item #42 as blocked by #65",
            "twig link predecessor 65 --id 66  Mark #66 as blocked by #65",
        ],
        ["link successor"] =
        [
            "twig link successor 66 --id 42          Mark item #42 as blocking #66",
            "twig link successor 66 --id 65  Mark #65 as blocking #66",
        ],
        ["link related"] =
        [
            "twig link related 615 --id 42           Relate item #42 to #615",
            "twig link related 615 --comment \"same root cause\" --id 42  Unsupported: refuses rather than dropping the comment",
            "twig link related 615 --id 619 -o json  Apply and verify #619's association",
        ],
        ["link unrelate"] =
        [
            "twig link unrelate 615 --id 42          Remove the related link to #615",
            "twig link unrelate 615 --id 619  Remove #619's related link to #615",
        ],
        ["link local related"] =
        [
            "twig link local related 43 --id 42  Verify a LOCAL association without native mutation",
            "twig link local related <seed-uuid> --id 42 -o json  Journal one local CAS effect",
        ],
        ["link local unrelate"] =
        [
            "twig link local unrelate 43 --id 42  Tombstone an existing LOCAL association",
            "twig link local unrelate <seed-uuid> --id 42 -o json  Verify local CAS; native reads may occur",
        ],
        ["link local list"] =
        [
            "twig link local list 42  List cached LOCAL edges with zero HTTP",
            "twig link local list --id <seed-uuid> -o json  Read original edge identities and local versions",
        ],
        ["link unlink"] =
        [
            "twig link unlink predecessor 65 --id 42   Remove the predecessor link to #65",
            "twig link unlink related 615 --id 42      Remove the related link to #615",
            "twig link unlink successor 66 --id 65  Remove #65's successor link to #66",
        ],
        ["link artifact"] =
        [
            "twig link artifact https://example.com/doc --name \"Plan\" --id 42  Add a hyperlink",
            "twig link artifact vstfs:///Git/Commit/p/r/abc123 --id 42         Add an artifact link",
            "twig link artifact https://example.com --id 42            Link to a specific item",
        ],
        ["note"] =
        [
            "twig note --id 42 --text \"Investigated root cause\"  Add a note",
            "twig note --id 42 --file notes.md  Read the note body from a file",
        ],
        ["update"] =
        [
            "twig update System.Title \"New title\" --id 42           Update the title field",
            "twig update System.Description \"<p>…</p>\" --format markdown --id 42  Update description",
        ],
        ["patch"] =
        [
            "twig patch --json '{\"System.Title\":\"New title\",\"System.State\":\"Doing\"}' --id 42  Patch multiple fields at once",
            "twig patch --stdin --format markdown --id 42   Read JSON from stdin, converting values from Markdown to HTML",
            "twig patch --json '{\"System.Title\":\"Fix\"}' --id 1234   Patch a specific work item by ID",
        ],
        ["edit"] =
        [
            "twig edit --id 42  Edit the description of #42",
            "twig edit --field System.Title --id 42  Edit the specified field",
        ],
        ["delete"] =
        [
            "twig delete 1234           Permanently delete work item #1234 from ADO",
            "twig delete 1234 --force   Delete without confirmation prompt",
        ],
        ["discard"] =
        [
            "twig discard 42 --yes  Discard local edits for #42",
            "twig discard --all --yes  Discard all retained remote-item edits",
        ],
        ["workspace"] =
        [
            "twig workspace --bench Focused -o json  Read saved Bench membership and protected drafts",
            "twig workspace --bench Focused --refresh -o ids  Pull only this named Bench without publishing drafts",
        ],
        ["workspace migrate"] =
        [
            "twig workspace migrate -o json  Review movement, backups and rollback preconditions without cutover",
            "twig workspace migrate --confirm <digest>  Explicitly authorize the exact reviewed admitted-ADO cutover",
            "twig workspace migrate --rollback --confirm <digest>  Return to protected originals when parity still holds",
            "twig workspace migrate --cancel-intent <intent-id> --confirm <digest>  Cancel preparation before a fresh review",
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
        // ADO #148 — Bench. The name is a plain argument, which spec §8 flags as PROVISIONAL:
        // whether a Bench is addressed the same way as a Context is a later ruling.
        ["bench create"] =
        [
            "twig bench create \"release blockers\"   Create a Bench you can return to",
            "twig bench create \"bugs I own\" -o json  Create one, reporting the result as JSON",
        ],
        ["bench list"] =
        [
            "twig bench list                        List shared Benches",
            "twig bench list -o json                List them for a script to read",
            "twig bench list --include-management -o json   Capture stable IDs and complete contents for management",
        ],
        ["bench rename"] =
        [
            "twig bench rename \"release blockers\" \"Release readiness\"   Rename a shared Bench",
            "twig bench rename <guid> \"Release readiness\" --expect-contents <digest> -o json   Rename only the captured definition",
        ],
        // ADO #150 — deleting. There is deliberately NO --force: a Bench holding pins reports
        // them and stops, and the way past that is re-typing its NAME, which differs every time
        // and so cannot become the unread reflex a flag becomes.
        ["bench delete"] =
        [
            "twig bench delete \"release blockers\"   Delete it (reports what it holds first)",
            "twig bench delete \"release blockers\" --confirm \"release blockers\"   Delete one that holds pins",
            "twig bench delete \"release blockers\" --confirm \"release blockers\" --expect-bench <id> --expect-contents <digest>   Delete only the reviewed arrangement",
        ],
        ["bench configuration"] =
        [
            "twig bench configuration              Read current Bench pins, areas, sprints and ownership",
            "twig bench configuration -o json      Capture raw settings digest for guarded edits",
        ],
        ["draft open"] =
        [
            "twig draft open 42 --bench Sprint      Open the shared native draft and served form",
            "twig draft open 42 --bench 7 --expect-binding <binding> --expect-identity <identity> -o json     Export editing envelope v1 with exact native tokens",
        ],
        ["draft stage"] =
        [
            "twig draft stage --stdin -o json       Send one version 1 JSON request on stdin and close it; never publishes",
            "twig draft stage --stdin -o minimal    Stage an exact nullable field batch using the native context and decimal tokens from OPEN",
        ],
        ["draft retain"] =
        [
            "twig draft retain --stdin -o json       Retain exactly the native context and decimal tokens from STAGE; close stdin after the v1 request",
            "twig draft retain --stdin -o minimal    Report the retained canonical digest without exporting, authorizing or applying it",
        ],
        ["draft review"] =
        [
            "twig draft review <digest>              Review exact retained effects by digest; no file or cache-enriched item context",
            "twig draft review <digest> -o json      Include captured baseline/proposed values and safe publisher provenance",
        ],
        ["draft show"] =
        [
            "twig draft show 42                        Inspect an existing native recoverable draft",
            "twig draft show 42 --document -o json     Export semantic inspection; may write local observation metadata, never applies",
        ],
        ["draft capture"] =
        [
            "twig draft capture 42 --bench Sprint --checkout-root <root> --connection-id <connection> --binding-id <binding> --identity-id <identity> --generation 1 --revision 3 -o json     Capture previously inspected native intent locally",
            "twig draft capture 42 --bench Sprint --checkout-root <root> --connection-id <connection> --binding-id <binding> --identity-id <identity> --generation 1 --revision 3 --recover <digest> -o json     Recover only the retained exact export",
        ],
        ["bench detail"] =
        [
            "twig bench detail 42                  Read full cached detail without changing the active item",
            "twig bench detail 42 --width 100 --expect-bench 7 -o json  Render detail for a captured Bench",
            "twig bench detail 42 --sync           Pull only this item and its links; never publish local edits",
            "twig bench detail 42 --document -o json  Read semantic fields and form metadata; no edit authority",
            "twig bench detail 42 --bench Sprint --document -o json --expect-bench 7  Cache-only explicit Bench read; usage error without --document/-o json or with --sync",
        ],
        ["bench configuration area candidates"] =
        [
            "twig bench configuration area candidates   List the configured team's official area paths",
            "twig bench configuration area candidates --expect-bench 7 -o json  Capture team paths and exact/under semantics",
        ],
        ["bench configuration area add"] =
        [
            "twig bench configuration area add \"Project\\Team A\"          Add an Under filter on this Bench",
            "twig bench configuration area add \"Project\\Team A\" --exact -o json  Match only this exact area",
        ],
        ["bench configuration area remove"] =
        [
            "twig bench configuration area remove \"Project\\Team A\"         Remove this Bench's area filter",
            "twig bench configuration area remove \"Project\\Team A\" --expect-bench 7 --expect-settings <digest> -o json  Guard a captured edit",
        ],
        ["bench configuration sprint add"] =
        [
            "twig bench configuration sprint add @Current+1   Follow next sprint using the cached calendar",
            "twig bench configuration sprint add \"Project\\Sprint 8\" -o json  Follow an absolute iteration path",
        ],
        ["bench configuration sprint remove"] =
        [
            "twig bench configuration sprint remove @Current  Remove the current-sprint expression",
            "twig bench configuration sprint remove \"Project\\Sprint 8\" --expect-settings <digest> -o json  Refuse stale settings",
        ],
        ["workspace area sync"] =
        [
            "twig workspace area sync                          Refresh native planning metadata; shared Bench rules are not replaced",
            "twig workspace area sync -o json                  Areas and exact-team calendar, not work-item synchronization",
        ],
        ["sprint"] =
        [
            "twig workspace             Static Core Bench read replaces the unsupported legacy sprint layout",
            "twig workspace --bench Focused -o json  Read a named scope without switching selection",
        ],
        ["config"] =
        [
            "twig config organization                 Show the configured organization",
            "twig config display.icons unicode -o json    Set the workspace icon preference",
        ],
        ["migrate-config"] =
        [
            "twig migrate-config            Split legacy .twig/config into twig.json + user prefs (AB#3296)",
            "twig migrate-config --dry-run  Preview the migration without writing any files",
        ],
        ["config status-fields"] =
        [
            "twig config status-fields                    Show configured status fields",
            "twig config status-fields --output json      Output status fields as JSON",
        ],
        ["auth status"] =
        [
            "twig auth status               Inspect refresh-token store + cached ADO access token",
            "twig auth status --output json  Output token diagnostics as JSON",
            "twig auth status --provider github --identity personal -o json  Inspect local app-user expiry without HTTP or refresh",
        ],
        ["auth clear"] =
        [
            "twig auth clear                               Invalidate the attached binding's access/admission proof",
            "twig auth clear --identity work               Clear only the 'work' identity's cached admission proof (stored credentials unchanged)",
            "twig auth clear --identity work --output json  Emit the clear result as JSON for scripts",
        ],
        ["auth login"] =
        [
            "twig auth login --provider github --identity personal --client-id <registered-app-client-id>  GitHub App device flow, no secret",
            "twig auth login --identity work                   Enroll (or re-enroll) the 'work' identity via loopback PKCE",
            "twig auth login --identity personal --tenant <tenant-id>   Enroll against a specific AAD tenant",
            "twig auth login --identity work --device-code     Device-code grant (headless boxes; often blocked by tenant policy)",
            "twig auth login --identity work --no-browser      Print the authorize URL instead of launching the browser",
            "twig auth login                                   Renew the identity named by the current attached binding",
        ],
        ["auth identities"] =
        [
            "twig auth identities                   List registered AAD and PAT identities with safe principal metadata",
            "twig auth identities --output json     Emit the identity list as JSON for scripts",
        ],
        ["auth pat"] =
        [
            "twig auth pat                                  Renew the attached PAT binding; prompts for the token with input hidden",
            "twig auth pat --identity work --org contoso    Enroll (or renew) 'work' at https://dev.azure.com/contoso",
            "twig auth pat --stdin --identity work --org contoso  Read a token from stdin (e.g. a pipe), never argv",
            "twig auth pat --identity work --output json           Renew a registered identity at its saved authority",
        ],
        ["connection add"] =
        [
            "twig connection add --provider ado --org contoso --project web   Register ADO dimensions without selecting credentials",
            "twig connection add --provider github --host github.com --repository owner/repo   Register a GitHub connection",
            "twig connection add --provider twig --database personal          Register a named native database",
        ],
        ["connection bind"] =
        [
            "twig connection bind --provider ado --org contoso --project web --identity work   Bind your registered ADO identity",
            "twig connection bind --provider github --host github.com --repository owner/repo --account me   Bind your named gh account",
            "twig connection bind --provider github --host github.com --repository owner/repo --identity me --client-id Iv1.example   Bind a GitHub App identity",
        ],
        ["connection list"] =
        [
            "twig connection list                                 List all registered connections across workspaces",
            "twig connection list --provider ado --org contoso --project web   Filter by explicit provider dimensions",
            "twig connection list --output json                   Emit connections and user credential selectors as JSON",
        ],
        ["connection status"] =
        [
            "twig connection status                 Show this workspace's requirement and local binding readiness",
            "twig connection status --output json   Emit recorded connection selection, not fresh access evidence",
        ],
        ["connection check"] =
        [
            "twig connection check                 Inspect every native unfinished-work/claim blocker without changing identity",
            "twig connection check --output json   Emit exact blocker identities and observed selection revisions",
        ],
        ["connection capabilities"] =
        [
            "twig connection capabilities                 Show relationship and fixed plan mutation capabilities",
            "twig connection capabilities --output json   Emit declarations and availability; GitHub writes require observed-non-atomic plans",
            "twig connection capabilities --github-schema -o json   Discover GitHub schema support without repository value catalogs",
        ],
        ["provider discover github"] =
        [
            "twig provider discover github --host github.com --repository owner/repo    Observe identity without binding",
            "twig provider discover github --host github.com --repository owner/repo -o json    Capture expected IDs and revision",
        ],
        ["provider bind github"] =
        [
            "twig provider bind github --host github.com --repository owner/repo --expected-owner-id O_id --expected-repository-id R_id --expected-attachment-revision 0    Bind the expected observation",
            "twig provider bind github --host github.com --repository owner/repo --expected-owner-id O_id --expected-repository-id R_id --expected-attachment-revision 4 --replace-binding <current-binding-id> -o json    Explicitly replace a selected identity",
        ],
        ["connection migrate"] =
        [
        ],
        ["connection pin"] =
        [
            "twig connection pin --binding <binding-id> --output json   Preview the checkout pin and exact safety blockers",
            "twig connection pin --binding <binding-id> --confirm <digest>   Apply or recover the exact guarded transition; reconnect affected hosts explicitly",
        ],
        ["connection unpin"] =
        [
            "twig connection unpin --output json   Preview returning this checkout to the endpoint's default binding",
            "twig connection unpin --confirm <digest>   Apply or recover the exact pin removal without publishing or discarding work",
        ],
        ["connection default"] =
        [
            "twig connection default --binding <id> --output json   Preview every affected unpinned attachment and all-member safety blockers",
            "twig connection default --binding <id> --confirm <digest>   Apply or recover the exact native family; pinned attachments remain unchanged",
        ],
        ["connection writes"] =
        [
            "twig connection writes   Inspect native uncertain writes and preserved acknowledgments for this checkout",
            "twig connection writes --output json   Emit immutable request digests, original identities and exact evidence blockers",
        ],
        ["connection reconcile-write"] =
        [
            "twig connection reconcile-write --intent <id> --confirm <digest> --rationale <basis>   Prove a CAS-bound outcome through original-actor readback",
            "twig connection reconcile-write --intent <id> --confirm <digest> --rationale <basis> --output json   Emit the native receipt or unresolved evidence blocker; never replay a write",
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
        ["plan validate"] =
        [
            "twig plan validate --file plan.json    Validate canonical file format offline, without application authority.",
            "twig plan validate --file plan.json --output json    Report the exact digest and validation scope; preview checks current evidence.",
        ],
        ["policy assess"] =
        [
            "twig policy assess --ids 42,43    Assess cached current compliance, not plan enforcement.",
            "twig policy assess --ids 42,43 -o json    Emit typed findings, evidence digest and explicit alternatives.",
        ],
        ["policy reconcile"] =
        [
            "twig policy reconcile --ids 42,43 --evidence <digest> --finding f1 --strategy reparent --parent 43    Project an explicitly selected repair.",
            "twig policy reconcile --ids 42 --evidence <digest> --finding f1 --strategy detach-parent -o json --plan-only > repair.json    Author a native plan; preview and apply with its digest.",
        ],
        ["plan preview"] =
        [
            "twig plan preview --file plan.json     Check current application evidence and retain reviewed intent without publishing.",
            "twig plan preview --file plan.json --expect-digest <digest>    Refuse changed canonical intent before retention.",
            "twig plan preview --file plan.json --output json    Emit machine preview for scripts.",
        ],
        ["plan apply"] =
        [
            "twig plan apply --file plan.json --confirm <digest>    Apply the exact reviewed plan.",
            "twig plan apply --file plan.json --confirm <digest> --output json    Emit every operation's journal outcome.",
        ],
        ["plan status"] =
        [
            "twig plan status --file plan.json      Show journal state for the plan's digest.",
            "twig plan status --file plan.json --output json    Emit exact attempt states, receipts and uncertainty evidence without replay.",
        ],
        ["pending"] =
        [
            "twig pending                           Inspect captured drafts and seed links in retained draft order.",
            "twig pending --output json             Emit typed original/proposed values and durable local identities without refreshing or flushing.",
        ],
        ["skills install"] =
        [
            "twig skills install --provider omp --target /path/to/skills    Install the canonical Twig skill.",
            "twig skills install --provider hermes --target /path/to/skills -o json    Prepare an explicit profile root.",
        ],
        ["skills update"] =
        [
            "twig skills update --provider omp --target /path/to/skills    Update only Twig-owned guidance.",
            "twig skills update --provider copilot --target /path/to/skills -o json    Preserve user companions and settings.",
        ],
        ["skills configure"] =
        [
            "twig skills configure --provider omp --target /path/to/skills --scenario terminal --companion my-presenter    Select an additive companion.",
            "twig skills configure --provider omp --target /path/to/skills --scenario terminal --clear    Return to base guidance.",
        ],
        ["skills status"] =
        [
            "twig skills status --provider hermes --target /path/to/skills    Inspect package identity and integrity.",
            "twig skills status --provider copilot --target /path/to/skills --scan-root /path/to/other-skills -o json    Check bounded shadowing.",
        ],
        ["skills add"] =
        [
            "twig skills add --provider omp --target /path/to/skills --source /path/to/my-presenter    Add separately supplied guidance.",
            "twig skills add --provider copilot --target /path/to/skills --source /path/to/my-integration -o json    Install an external package without executing it.",
        ],
    };

    /// <summary>
    /// Resolves the command name from <paramref name="args"/> and prints usage examples
    /// if any are registered. Tries the longest compound key first
    /// (e.g. <c>"workspace area add"</c>), then two-token (e.g. <c>"nav up"</c>),
    /// then single-token (e.g. <c>"set"</c>). Does nothing if no examples match.
    /// </summary>
    internal static void ShowIfPresent(string[] args)
    {
        if (args.Length == 0) return;
        if (Twig.CoreMutationAdmissionGuard.ValidateCatalog(args) is not null) return;

        string? key = null;

        for (var length = args.Length; length > 0; length--)
        {
            var candidate = string.Join(" ", args, 0, length);
            if (!Examples.ContainsKey(candidate)) continue;
            key = candidate;
            break;
        }

        if (key is null) return;

        if (!Examples.TryGetValue(key, out var examples))
            return;

        Console.WriteLine();
        Console.WriteLine("Examples:");
        foreach (var example in examples)
            Console.WriteLine($"  {example}");

        if (key == "plan validate")
        {
            var sample = new Plan(new(new("github"), "example/work-items", "example-destination", "example-identity"),
                "example-schema-fingerprint", 0,
                [new("create-seed", OpKind.Create, ItemKey.Local(Guid.Parse("11111111-1111-7111-8111-111111111111")),
                    fields: new([KeyValuePair.Create("github.title", FieldValue.String("An example work item"))]),
                    type: "github.issue", concurrency: ConcurrencyMode.ObservedNonAtomic)]);
            Console.WriteLine();
            Console.WriteLine("Complete seed creation Plan (format example only; not ready to apply):");
            Console.WriteLine(Encoding.UTF8.GetString(PlanModel.PlanBytes(sample)));
            Console.WriteLine("Use the UUID and typed values of an actual seed (`twig pending -o json`) and the exact");
            Console.WriteLine("connection, store version, schema and policy evidence for your target.");
            var version = VersionHelper.GetVersion();
            var reference = version.Contains('-') ? "main" : $"v{version}";
            Console.WriteLine($"Format reference: https://github.com/{CompanionStartup.ResolveRepoSlug()}/blob/{reference}/docs/features/plans.md");
        }
    }
}
