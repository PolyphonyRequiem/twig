using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Domain.Services.Mutation;
using Twig.Infrastructure.Services.Mutation;
using Twig.Infrastructure.Config;
using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Mcp.Services;
using Twig.RenderTree;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tool for process discovery: <c>twig_process</c>.
/// Lists all work item types (no args) or shows type details (with type name).
/// </summary>
[McpServerToolType]
public sealed class ProcessTools(ConnectionResolver resolver)
{
    [McpServerTool(Name = "twig_process"), Description("Show process configuration: list types (no args) or type details (with type name)")]
    public async Task<CallToolResult> Process(
        [Description("Work item type name to show details for (omit to list all types)")] string? type = null,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        CallToolResult toolResult;
        if (type is null)
            toolResult = await ListTypesAsync(ctx, ct);
        else
            toolResult = await ShowTypeDetailAsync(ctx, type, ct);

        return await EnvelopeBuilder.WrapAsync(ctx, toolResult, verbose, ct);
    }

    private static async Task<CallToolResult> ListTypesAsync(ConnectionScope ctx, CancellationToken ct)
    {
        var types = await ctx.Get<IProcessTypeStore>().GetAllAsync(ct);

        if (types.Count == 0)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.CacheStale, "No process types found. Use twig_sync to refresh process data.", ctx, ct);

        return McpResultBuilder.FormatProcessList(types);
    }

    private static async Task<CallToolResult> ShowTypeDetailAsync(
        ConnectionScope ctx, string typeName, CancellationToken ct)
    {
        var typeRecord = await ctx.Get<IProcessTypeStore>().GetByNameAsync(typeName, ct);

        if (typeRecord is null)
            return await EnvelopeBuilder.ErrorAsync(McpErrorCode.ItemNotFound, $"Type '{typeName}' not found. Use twig_sync to refresh process data.", ctx, ct);

        var fields = await ctx.Get<IFieldDefinitionStore>().GetAllAsync(ct);
        return McpResultBuilder.FormatProcessType(typeRecord, fields);
    }

    /// <summary>
    /// <c>twig_process_description</c> — a structural description of NAMED TYPES ONLY, so an
    /// agent does not pay for a whole-process document it did not ask for (AB#241).
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>A SIBLING tool rather than an option on <c>twig_process</c>, and the spec left that
    /// call to the build (Implementation Decision 10).</b> Three reasons, in order of weight.
    /// First, the two differ by COST CLASS: <c>twig_process</c> reads the local cache and issues
    /// no network call at all, while this one is an uncached live fetch of roughly half a
    /// megabyte taking seconds — folding them together would hide that behind one name, and an
    /// agent choosing a tool reads the name before it reads the description. Second, they have
    /// different failure modes: <c>twig_process</c> fails with "run twig_sync", this one fails
    /// when the org is unreachable, and a single tool would have to explain both. Third,
    /// <c>twig_process</c>'s <c>type</c> argument already means "show me this ONE type's cached
    /// detail"; this surface takes a LIST, so overloading that argument would make one parameter
    /// mean two things depending on a sibling flag.
    /// </para>
    /// <para>
    /// 🔴 <b>The document is produced by the SAME assembler and the SAME projection the CLI
    /// uses</b> — <see cref="ProcessDescriptionAssembler"/> then
    /// <see cref="ProcessDescriptionDocument"/> — and rendered by the same
    /// <see cref="JsonRenderer"/>. Not "the same shape": the same code, so the bytes are
    /// identical by construction rather than by convention. This surface decides NOTHING about
    /// the document. That is what acceptance criterion 2 asks for, and it is why the projection
    /// was moved into the domain layer by this ticket.
    /// </para>
    /// <para>
    /// 🔴 <b>Selection is only ever WHICH TYPES. There is deliberately no argument naming which
    /// PARTS of a type to return, and adding one is forbidden</b> (Decision 10, Solution S3): a
    /// filtered document lets a real difference hide in the part that was dropped, with the
    /// reader unable to tell anything was. Omitting <paramref name="types"/> entirely yields the
    /// whole process, matching the CLI verb's default.
    /// </para>
    /// </remarks>
    [McpServerTool(Name = "twig_process_description"), Description(
        "Structural description of the ADO process — types, fields, requiredness, picklist values, states, transitions, rules, backlog membership, form layout. Name types to describe only those (cheaper); omit to describe every type. Live uncached fetch. Byte-identical to 'twig process description -o json'.")]
    public async Task<CallToolResult> ProcessDescription(
        [Description("Work item type REFERENCE names to describe (omit to describe every type in the process). Selection is by type only — there is no way to select parts of a type.")] string[]? types = null,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        // 🔴 An empty array is REJECTED, not read as "every type". The schema already sets
        // minItems=1 so a well-behaved client cannot send one, but this surface is also
        // reachable via twig_batch and by clients that do not validate — and the two silent
        // readings available here are both bad. Treating it as "every type" answers a probable
        // mistake (a caller whose selection came out empty) with the most expensive document
        // twig can produce; treating it as "no types" renders an empty document that reads as
        // "this process has no types", which is the silent-omission failure this whole feature
        // exists to prevent. Saying so is the only honest option.
        if (types is { Length: 0 })
        {
            return await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.InvalidInput,
                "'types' was an empty list. Omit it entirely to describe every type in the "
                    + "process, or name the types you want.",
                ctx, ct);
        }

        string document;
        try
        {
            document = await RenderDocumentAsync(ctx, types, ct);
        }
        catch (ProcessDescriptionTypeNotFoundException ex)
        {
            // 🔴 A hard error, matching the CLI. Describing the types that DO exist and staying
            // silent about the one that does not would hand an agent a document that looks
            // complete and answers a question it did not ask.
            return await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.ItemNotFound,
                $"Work item type '{ex.TypeReferenceName}' does not exist in this process. "
                    + "Use twig_process to list types.",
                ctx, ct);
        }

        if (document.Length == 0)
        {
            return await EnvelopeBuilder.ErrorAsync(
                McpErrorCode.AdoUnreachable,
                "Could not describe this project's process. The process may not be reachable, "
                    + "or this project does not resolve to one.",
                ctx, ct);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            // 🔴 WriteRawValue, NOT JsonDocument.Parse + WriteTo. This is a byte-identity
            // defect found by running both surfaces against the real org (AB#241), and the
            // unit tests could not see it because it lives in the ENVELOPE rather than in the
            // document: `JsonRenderer` writes with the default encoder while the envelope
            // writes with UnsafeRelaxedJsonEscaping, so re-writing a parsed document through
            // the envelope's writer RE-ENCODES it. Six lines differed on the live document —
            // `\u0027` became `'`, `\u0026` became `&`, `\u002B` became `+`. Every one is
            // valid JSON carrying the same string value, which is precisely why it would have
            // survived any structural assertion and shipped: acceptance criterion 2 asks for
            // BYTE-identity, and "the same after both sides are re-parsed" is the weaker claim
            // that lets two formats drift apart while a test says they agree.
            //
            // WriteRawValue emits the bytes verbatim, so the document inside the envelope is
            // the document the CLI writes — including its own indentation, which the writer
            // does not re-indent.
            writer.WritePropertyName("description");
            writer.WriteRawValue(document);
        }, verbose, ct);
    }

    /// <summary>
    /// Assembles and renders the description document, returning the exact bytes the CLI's
    /// <c>-o json</c> writes. Empty when the process could not be resolved.
    /// </summary>
    /// <remarks>
    /// 🔴 Separated from the envelope so a test can assert THIS string against the CLI's output.
    /// Asserting against the enveloped result would compare the transport too and could pass on
    /// a document that differed, or fail on an envelope change that did not touch the document.
    /// </remarks>
    internal static Task<string> RenderDocumentAsync(
        ConnectionScope ctx, IReadOnlyList<string>? typeReferenceNames, CancellationToken ct)
        => RenderDocumentAsync(
            ctx.Get<ProcessDescriptionAssembler>(),
            ctx.Get<TimeProvider>(),
            typeReferenceNames,
            ct);

    /// <summary>
    /// The agent surface's document, from an assembler and a clock.
    /// </summary>
    /// <remarks>
    /// 🔴 Takes its two dependencies directly rather than a <see cref="ConnectionScope"/> so the
    /// byte-identity test can drive THIS METHOD — the real one the tool calls — against the same
    /// scripted source the CLI test uses. The alternative was a test that reimplemented the MCP
    /// path with its own assembler and renderer calls, which would have compared the CLI against
    /// a copy of the MCP surface rather than against the MCP surface, and would have stayed green
    /// through any change to the real one. The scope overload above is a two-line resolve, so
    /// nothing meaningful sits outside what the test covers.
    /// </remarks>
    internal static async Task<string> RenderDocumentAsync(
        ProcessDescriptionAssembler assembler,
        TimeProvider time,
        IReadOnlyList<string>? typeReferenceNames,
        CancellationToken ct)
    {
        var description = await assembler.AssembleAsync(
            typeReferenceNames,
            // The one permitted variance, injected the same way the CLI injects it so the
            // clock is the only thing that can differ between the two surfaces' documents.
            time.GetUtcNow(),
            ct);

        if (description is null)
            return string.Empty;

        // 🔴 The SHARED render, settings included. Not `new JsonRenderer(buffer, indented: true)`
        // here: independent review caught that as the last place byte-identity still rested on
        // two literals agreeing rather than on shared code. Sharing the assembler and the
        // projection makes both surfaces build the same render tree, but a tree is not bytes —
        // the writer's options decide those too.
        //
        // 🔴 The complete rendering is not a choice this surface makes. An agent has no `-o`,
        // and the abridged rendering exists for a human reading a terminal; handing an agent the
        // summary would give it a document that omits detail. The point of this surface is fewer
        // TYPES, never fewer parts of a type.
        return ProcessDescriptionDocument.Render(description);
    }
}
