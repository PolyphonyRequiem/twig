using System.Reflection;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Shouldly;
using Twig.Domain.Services.Process;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// AB#241 — the agent-surface half of the process description guarantees.
/// </summary>
/// <remarks>
/// <para>
/// The byte-identity assertion itself (spec test 14) lives in
/// <c>Twig.Cli.Tests.Commands.ProcessDescriptionAgentSurfaceTests</c>, because it needs the CLI
/// command, its formatter and its renderer factory, and those are only reachable from that
/// project. What CANNOT be asserted there is anything about <see cref="ProcessTools"/> itself:
/// <c>Twig.Cli.Tests</c> deliberately does not reference <c>Twig.Mcp</c>.
/// </para>
/// <para>
/// 🔴 <b>That non-reference is load-bearing and was learned from a red CI run.</b> An earlier
/// revision of this ticket had <c>Twig.Cli.Tests</c> reference <c>Twig.Mcp</c> so one test could
/// drive both surfaces. <c>Twig.Mcp</c> is an EXECUTABLE, so the reference copied <c>twig-mcp</c>
/// into the Cli suite's output — and <c>BinaryLauncherTests</c> clears <c>PATH</c> specifically to
/// assert that binary is NOT discoverable. It duly launched the real MCP host in-process, which
/// crashed the Cli test host 48 tests in. It went green locally only because AGENTS.md's canonical
/// runner excludes <c>BinaryLauncher</c> for an unrelated environmental reason.
/// </para>
/// <para>
/// So the guarantees are split by what each project can legally see, and this file carries the
/// half that needs the tool type.
/// </para>
/// </remarks>
public sealed class ProcessDescriptionToolTests
{
    /// <summary>
    /// 🔴 The tool offers TYPE selection and nothing that names a part of a type.
    /// </summary>
    /// <remarks>
    /// Acceptance criterion 3, the agent-surface half. Per-part selection is forbidden
    /// (Implementation Decision 10, Solution S3): it is a filter, and a reader handed a filtered
    /// document cannot recover what was dropped and cannot tell that anything was.
    /// <para>
    /// The precondition is stated rather than assumed — the parts a filter would select over are
    /// named explicitly, so this is a claim about a real vocabulary rather than a tautology over
    /// words the surface never uses.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheTool_OffersNoPerPartSelection()
    {
        string[] parts = ["fields", "states", "transitions", "rules", "behaviours", "layout"];

        var parameters = typeof(ProcessTools)
            .GetMethod(nameof(ProcessTools.ProcessDescription))!
            .GetParameters()
            .Select(p => p.Name!)
            .ToArray();

        parameters.ShouldBe(["types", "workspace", "verbose", "ct"]);

        foreach (var part in parts)
        {
            foreach (var parameter in parameters)
            {
                parameter.Contains(part, StringComparison.OrdinalIgnoreCase).ShouldBeFalse(
                    $"parameter '{parameter}' names the part '{part}' — per-part selection is "
                    + "forbidden (AB#241, Implementation Decision 10, Solution S3).");
            }
        }
    }

    /// <summary>
    /// 🔴 The tool's document comes from the SHARED projection, not from a serializer of its own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the link that makes the CLI-side byte-identity test cover the real tool. That test
    /// drives <see cref="ProcessDescriptionDocument.Render"/>; this one proves the tool's own
    /// render path produces exactly what that shared method produces. Without it, the two projects
    /// could drift: the shared render could stay byte-identical to the CLI while the tool quietly
    /// stopped using it.
    /// </para>
    /// <para>
    /// 🔴 Asserted BEHAVIOURALLY — the tool's real method is run against a scripted source and its
    /// output compared to the shared render's. An earlier version of this test scanned the
    /// method's IL for the call, which fails for a reason worth recording: an <c>async</c> method's
    /// body is moved into a compiler-generated state machine, so the outer method contains no such
    /// call and the scan reported a false negative against correct code. Comparing output is both
    /// simpler and stronger — it fails for any second serializer, including one that happens to
    /// call the shared method and then post-process the result.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheTool_RendersThroughTheSharedProjection()
    {
        var source = new ScriptedDescriptionSource();
        var assembler = new ProcessDescriptionAssembler(source)
        {
            RouteVersions = [new ProcessDescriptionRouteVersion("work/processes/{id}/workItemTypes", "7.1-preview.2")],
        };

        var capturedAt = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        var viaTool = await ProcessTools.RenderDocumentAsync(
            assembler, new FrozenTimeProvider(capturedAt), null, CancellationToken.None);

        var description = await assembler.AssembleAsync(null, capturedAt, CancellationToken.None);
        var viaSharedRender = ProcessDescriptionDocument.Render(description!);

        // PRECONDITION: a real document, not an empty string — byte-identity between two empty
        // results would satisfy the assertion while proving nothing.
        viaTool.Length.ShouldBeGreaterThan(100);
        viaTool.ShouldContain("descriptorVersion");

        viaTool.ShouldBe(
            viaSharedRender,
            "ProcessTools.RenderDocumentAsync must render through ProcessDescriptionDocument.Render "
            + "— a second serializer on this surface is exactly the drift acceptance criterion 2 "
            + "forbids (AB#241).");
    }

    private sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A minimal in-memory description source — one type, enough to render a document.</summary>
    private sealed class ScriptedDescriptionSource : IProcessDescriptionSource
    {
        public Task<ProcessIdentity?> GetProcessIdentityAsync(CancellationToken ct) =>
            Task.FromResult<ProcessIdentity?>(new ProcessIdentity(
                "https://dev.azure.com/Test", "Test", "process-id", "Niflheim"));

        public Task<IReadOnlyList<ProcessTypeSummary>?> GetTypesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProcessTypeSummary>?>(
                [new ProcessTypeSummary("Niflheim.Grilling", "Grilling", "", "custom", null, false)]);

        public Task<ProcessTypeDetail?> GetTypeDetailAsync(
            string typeReferenceName, string? inheritsFrom, CancellationToken ct) =>
            Task.FromResult<ProcessTypeDetail?>(new ProcessTypeDetail(
                Fields: [new ProcessTypeField("System.Title", "Title", "string", null, true, "system", false, "")],
                States: [new ProcessTypeState("To do", "Proposed", 1, "b2b2b2", "custom", false)],
                Transitions: [new ProcessTypeTransition("", "To do")],
                Unfetched: null,
                Rules: [],
                Behaviours: [],
                Layout: null));

        public Task<IReadOnlyDictionary<string, FieldValueConstraint>?> GetFieldValueConstraintsAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, FieldValueConstraint>?>(null);

        public Task<IReadOnlyList<ProcessBehaviourSummary>?> GetBehaviourCatalogueAsync(
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProcessBehaviourSummary>?>(null);
    }
}
