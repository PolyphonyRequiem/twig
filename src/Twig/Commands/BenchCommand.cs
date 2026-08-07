using Twig.Domain.Services.Mutation;
using Twig.Formatters;
using Twig.Infrastructure.Services.Mutation;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig bench create &lt;name&gt;</c> and <c>twig bench list</c> (ADO #148,
/// docs/specs/bench.spec.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Every decision about what a Bench IS belongs to <see cref="BenchWorkflow"/>, the shared
/// mutation-workflow seam the agent surface also routes through. This adapter resolves the target
/// and renders the outcome and nothing else, so the two surfaces cannot disagree about what
/// creating or listing a Bench means.
/// </para>
/// <para>
/// 🔴 The output format is DECLARED by the caller through <c>-o</c>. twig never sniffs for a
/// terminal: the machine-readable listing is produced because a script asked for it, not because
/// no tty was attached, so a command means the same thing in a pipe as at a prompt.
/// </para>
/// </remarks>
public sealed class BenchCommand(
    BenchWorkflow benchWorkflow,
    OutputFormatterFactory formatterFactory,
    RendererFactory? rendererFactory = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();

    /// <summary>Create a Bench with a name the person will recognise later.</summary>
    public async Task<int> CreateAsync(
        string name,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var outcome = await benchWorkflow.CreateAsync(name, ct);

        switch (outcome)
        {
            case BenchOutcome.Created created:
                RenderOutcome(
                    "benchCreated",
                    $"Created Bench '{created.Bench.Name}'.",
                    created.Bench.Name,
                    outputFormat,
                    Severity.Success);
                return 0;

            // A name collision is the person's to resolve, so it exits non-zero and names both the
            // name asked for and the one already stored — the two can differ only in case, and a
            // message that showed one of them would look like a contradiction.
            case BenchOutcome.NameAlreadyExists exists:
                Console.Error.WriteLine(fmt.FormatError(
                    $"A Bench named '{exists.Existing.Name}' already exists. " +
                    "Choose another name, or use the one you have."));
                return 1;

            case BenchOutcome.NameRejected rejected:
                Console.Error.WriteLine(fmt.FormatError(rejected.Reason));
                return 2;

            default:
                Console.Error.WriteLine(fmt.FormatError("Unrecognised outcome creating a Bench."));
                return 1;
        }
    }

    /// <summary>
    /// Put one arrangement down and pick another up.
    /// </summary>
    /// <remarks>
    /// 🔴 An unknown name exits NON-ZERO — a script's pipeline stops rather than proceeding
    /// against the wrong list — names what was asked for, says what to do, and creates nothing.
    /// The exit code is what a script sees; the message is what a person sees; both come from the
    /// one workflow outcome, so the two surfaces cannot disagree about whether a Bench exists.
    /// </remarks>
    public async Task<int> SwitchAsync(
        string name,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var outcome = await benchWorkflow.SwitchAsync(name, ct);

        switch (outcome)
        {
            case BenchOutcome.Switched switched:
                RenderOutcome(
                    "benchSwitched",
                    $"Now on Bench '{switched.Bench.Name}' (was '{switched.PreviousBenchName}').",
                    switched.Bench.Name,
                    outputFormat,
                    Severity.Success);
                return 0;

            case BenchOutcome.UnknownBench unknown:
                var known = unknown.KnownBenchNames.Count == 0
                    ? "There are no Benches yet."
                    : "Benches that exist: " + string.Join(", ", unknown.KnownBenchNames) + ".";
                Console.Error.WriteLine(fmt.FormatError(
                    $"There is no Bench named '{unknown.RequestedName}'. {known} " +
                    $"Create it with: twig bench create \"{unknown.RequestedName}\""));
                return 1;

            case BenchOutcome.NameRejected rejected:
                Console.Error.WriteLine(fmt.FormatError(rejected.Reason));
                return 2;

            default:
                Console.Error.WriteLine(fmt.FormatError("Unrecognised outcome switching Bench."));
                return 1;
        }
    }

    /// <summary>List the Benches that exist, marking the current one.</summary>
    public async Task<int> ListAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var listing = await benchWorkflow.ListAsync(ct);
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();

        if (lower is "json" or "json-full" or "json-compact" or "ids")
        {
            var columns = new List<RenderColumn>
            {
                new("name", "Name"),
                new("isCurrent", "Current"),
                new("isDefault", "Default"),
                new("selectors", "Selectors"),
            };

            var rows = new List<RenderRow>(listing.Benches.Count);
            foreach (var bench in listing.Benches)
            {
                var cells = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["name"] = RenderCell.String(bench.Name),
                    // "isCurrent", not "current": the document-level "current" names the Bench,
                    // and a per-row key of the same name would make a script reading either one
                    // find a boolean where it expected a name, or the reverse.
                    ["isCurrent"] = RenderCell.String(IsCurrent(bench.Name, listing) ? "true" : "false"),
                    ["isDefault"] = RenderCell.String(bench.IsDefault ? "true" : "false"),
                    ["selectors"] = RenderCell.Integer(bench.Selectors.Count),
                };
                rows.Add(new RenderRow("bench", cells));
            }

            var fields = new List<DocumentField>(3)
            {
                new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Count))),
                new("current", new RenderNode.KeyValue("current", RenderCell.String(listing.CurrentBenchName))),
                new("entries", new RenderNode.Table(null, columns, rows)),
            };

            _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[]
            {
                (RenderNode)new RenderNode.Document("benchList", fields),
            }));
            return 0;
        }

        var nodes = new List<RenderNode>(listing.Benches.Count + 1);
        foreach (var bench in listing.Benches)
        {
            var marker = IsCurrent(bench.Name, listing) ? "* " : "  ";
            nodes.Add(new RenderNode.Text($"{marker}{bench.Name}", Severity.Info));
        }
        nodes.Add(new RenderNode.Text(
            $"{listing.Benches.Count} bench(es). Current: {listing.CurrentBenchName}.", Severity.Info));

        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(nodes));
        return 0;
    }

    /// <summary>
    /// Names are compared case-insensitively, the same way the store matches them, so the marker
    /// cannot land on no row because a name was stored with different capitalisation.
    /// </summary>
    private static bool IsCurrent(string name, BenchListing listing)
        => string.Equals(name, listing.CurrentBenchName, StringComparison.OrdinalIgnoreCase);

    private void RenderOutcome(
        string kind, string message, string benchName, string outputFormat, Severity severity)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record(kind, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["name"] = RenderCell.String(benchName),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, severity),
        };
        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }
}
