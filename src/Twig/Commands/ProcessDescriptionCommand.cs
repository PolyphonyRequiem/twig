using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig process description [&lt;type&gt;]</c>: writes a byte-stable structural
/// description of an ADO process, so a caller can point an ordinary diff tool at two of them.
/// </summary>
/// <remarks>
/// <para>
/// A thin adapter, deliberately. It resolves the type argument and the output target and
/// decides NOTHING about the document — that all lives in
/// <see cref="ProcessDescriptionAssembler"/>. The placement is what makes "the agent surface
/// returns the same document with fewer types" a structural fact rather than a convention
/// two surfaces would drift apart on.
/// </para>
/// <para>
/// Switches mirror the shipped <c>twig process layout</c> — <c>--out</c>, <c>-o</c>, stdout
/// when <c>--out</c> is omitted, confirmation on the error stream so <c>--out</c> composes
/// in scripts — so a caller does not learn a second convention inside one command family.
/// </para>
/// <para>
/// 🔴 <b>Descriptor version 0.1 declares NO known gaps, and as of AB#238 that claim is
/// checked rather than assumed.</b> Every content item Implementation Decision 4 enumerates is
/// now carried: conditional requiredness by AB#236's rules merge, picklist values by AB#237's
/// constraint merge, and rules, behaviour membership and form layout by AB#238. The mechanism
/// is kept — see <see cref="ProcessDescriptionAssembler.KnownGaps"/> — and the human rendering
/// states the absence positively rather than printing a warning heading with nothing under it,
/// because "this document makes no reservations" is itself the claim a reader needs in order
/// to read a future non-empty list as meaningful.
/// </para>
/// <para>
/// 🔴 <b>The descriptor version stays 0.1 despite this ticket adding three content items.</b>
/// 0.1 is explicitly "still under design" and the form layout's shape was named on the record
/// as the reason it is not 1.0 — so shipping the layout does not settle it, and bumping now
/// would announce a stability this document does not yet have. Raised rather than decided
/// silently: it is a contract question.
/// </para>
/// <para>
/// Governing ruling: <c>docs/specs/process-description.spec.md (branch docs/process-descriptor-map)</c> Implementation Decisions
/// 1, 2, 3, 4, 8, 9, 11.
/// </para>
/// </remarks>
internal sealed class ProcessDescriptionCommand(
    ProcessDescriptionAssembler assembler,
    OutputFormatterFactory formatterFactory,
    RendererFactory rendererFactory,
    TimeProvider? timeProvider = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 🔴 The <c>-o</c> value that produces the COMPLETE document, and the one the abridged
    /// rendering's banner names.
    /// </summary>
    /// <remarks>
    /// Read by both the completeness test below and the banner, so the banner cannot come to
    /// name a format that does not produce the complete document. A banner pointing at a
    /// nonexistent format would satisfy a bare string-presence test while telling the reader a
    /// lie — this constant is what makes the test assert against the real value instead.
    /// <para>
    /// 🔴 FORWARDED to <see cref="ProcessDescriptionDocument.CompleteFormat"/> rather than
    /// declared again (AB#241). The banner is emitted by the shared projection now, so a second
    /// literal here would be the one thing this constant exists to prevent: two copies that can
    /// drift, with the test asserting against the copy the banner does not use.
    /// </para>
    /// </remarks>
    internal const string CompleteFormat = ProcessDescriptionDocument.CompleteFormat;

    /// <summary>
    /// Every <c>-o</c> value that yields the complete document.
    /// </summary>
    /// <remarks>
    /// 🔴 There is more than one, and treating <see cref="CompleteFormat"/> as the only one is
    /// a live defect rather than a tidiness point: <c>json-full</c> and <c>json-compact</c>
    /// are on the accept-list and resolve to the SAME JSON renderer. Labelling their output
    /// "abridged" would stamp a machine-complete document with a banner saying it omits
    /// detail — a false warning is as much a lie as a missing one, and it would send a reader
    /// looking for content that is already in front of them.
    /// <para>
    /// Derived from the format's normalized name so this cannot drift from the renderer's own
    /// aliasing.
    /// </para>
    /// </remarks>
    internal static bool IsCompleteFormat(string? outputFormat)
        => OutputFormats.Normalize(outputFormat) is "json" or "json-full" or "json-compact";

    /// <summary>
    /// Executes <c>twig process description [&lt;type&gt;] [--out path] [-o format]</c>.
    /// </summary>
    /// <param name="typeName">
    /// A type's REFERENCE name to describe just that type, or <c>null</c> for every type in
    /// the process. Naming one is the cheap path.
    /// </param>
    /// <param name="outPath">Optional file to write the rendered document to.</param>
    /// <param name="outputFormat">The rendering. <c>json</c> is complete; others are abridged.</param>
    public async Task<int> ExecuteAsync(
        string? typeName = null,
        string? outPath = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        // 🔴 `-o ids` is REFUSED, not served badly. That renderer emits only cells keyed "id"
        // whose value is an integer, and a process description has no numeric ids at all — so
        // it would produce an EMPTY file, silently, with a zero exit code. Worse, it is the
        // one format that structurally cannot carry the abridged self-declaration, so the
        // reader would get nothing and no notice that anything was dropped. An explicit error
        // naming the format that works is the honest outcome.
        if (string.Equals(OutputFormats.Normalize(outputFormat), "ids", StringComparison.Ordinal))
        {
            _stderr.WriteLine(fmt.FormatError(
                "'-o ids' cannot render a process description: the document contains no "
                + $"numeric ids. Use '-o {CompleteFormat}' for the complete document."));
            return 1;
        }

        ProcessDescription? description;
        try
        {
            description = await assembler.AssembleAsync(
                typeName is null ? null : [typeName],
                // Injected rather than read inside the assembler so a test can hold it fixed
                // and assert everything else is byte-identical.
                _time.GetUtcNow(),
                ct);
        }
        catch (ProcessDescriptionTypeNotFoundException ex)
        {
            // 🔴 A hard error, and NO partial file. A script that banked a document saying
            // "this process has nothing" when the truth is "you asked for something that is
            // not here" would be worse than a failure.
            _stderr.WriteLine(fmt.FormatError(
                $"Work item type '{ex.TypeReferenceName}' does not exist in this process. " +
                "Run 'twig process' to list types."));
            return 1;
        }

        if (description is null)
        {
            _stderr.WriteLine(fmt.FormatError(
                "Could not describe this project's process. The process may not be reachable, " +
                "or this project does not resolve to one."));
            return 1;
        }

        var tree = BuildTree(description, outputFormat);

        if (outPath is null)
        {
            rendererFactory.GetRenderer(outputFormat).Render(tree);
            return 0;
        }

        // 🔴 Rendered to a TEMPORARY file and moved into place. Writing straight to outPath
        // means a renderer that throws mid-render leaves a TRUNCATED document on disk — which
        // is worse than no file at all, because a truncated document is silently missing
        // types and a reader diffing it sees differences that are not real. The unknown-type
        // path already promises "no partial file"; this makes the promise hold for write
        // failures too.
        var tempPath = outPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using (var writer = new StreamWriter(tempPath, append: false))
            {
                rendererFactory.GetRenderer(outputFormat, writer).Render(tree);
            }

            File.Move(tempPath, outPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(tempPath);
            _stderr.WriteLine(fmt.FormatError($"Could not write '{outPath}': {ex.Message}"));
            return 1;
        }
        catch
        {
            // Any other failure mid-render must not leave the scratch file behind either.
            TryDelete(tempPath);
            throw;
        }

        // Confirmation on the error stream so `--out` stays silent on stdout and composes in
        // scripts; the file is the output. Same contract the layout command ships.
        _stderr.WriteLine(
            $"Wrote process description ({description.Types.Count} type(s), descriptor " +
            $"{description.Header.DescriptorVersion}) to {outPath}");
        return 0;
    }

    /// <remarks>
    /// Best-effort scratch cleanup. A failure to delete the temp file must never mask the
    /// real error the caller is being told about.
    /// </remarks>
    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>
    /// Projects the assembled description into the render tree that becomes the output.
    /// </summary>
    /// <remarks>
    /// 🔴 <b>Delegated, not implemented (AB#241).</b> The projection lives in
    /// <see cref="ProcessDescriptionDocument"/> in the domain layer so the agent surface emits
    /// the SAME bytes rather than its own document. Keeping it here would have put the
    /// projection inside the <c>twig</c> executable, which <c>Twig.Mcp</c> cannot reference —
    /// so the agent surface would have had to author a second serializer and byte-identity
    /// would have been a convention rather than a structural fact.
    /// <para>
    /// The completeness decision stays HERE because it is a CLI concern: it is a function of
    /// <c>-o</c>, which only this surface has. The shared projection takes the resolved flag.
    /// </para>
    /// </remarks>
    private static RenderTree.RenderTree BuildTree(ProcessDescription description, string outputFormat)
        => ProcessDescriptionDocument.BuildTree(description, IsCompleteFormat(outputFormat));
}
