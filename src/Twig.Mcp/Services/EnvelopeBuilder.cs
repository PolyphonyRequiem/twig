using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Twig.Mcp.Services;

/// <summary>
/// Builds MCP response envelopes with automatic context population.
/// Every MCP tool can call <see cref="SuccessAsync"/> or <see cref="Error"/> to produce a
/// consistent <c>{ success, data, context, hints }</c> envelope without repeating boilerplate.
/// </summary>
internal sealed class EnvelopeBuilder
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Builds a success envelope with automatically populated context.
    /// </summary>
    /// <param name="ctx">Workspace context for populating the <c>context</c> block. May be <c>null</c> for workspace-independent responses.</param>
    /// <param name="writeData">Callback that writes tool-specific fields into the <c>data</c> object.</param>
    /// <param name="verbose">When <c>true</c>, includes contextual hints in the response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="CallToolResult"/> containing the envelope JSON.</returns>
    public static async Task<CallToolResult> SuccessAsync(
        WorkspaceContext? ctx,
        Action<Utf8JsonWriter> writeData,
        bool verbose,
        CancellationToken ct)
    {
        var context = ctx is not null
            ? await BuildContextAsync(ctx, ct)
            : new McpContext(ActiveItemId: null, Workspace: "", CacheAge: "");

        IReadOnlyList<string> hints = verbose && ctx is not null
            ? await McpHintProvider.GetHintsAsync(ctx, ct)
            : [];

        return BuildEnvelopeJson(context, writeData, hints);
    }

    /// <summary>
    /// Builds a success envelope wrapping pre-built JSON (from an existing <see cref="CallToolResult"/>).
    /// Useful for migrating existing tools incrementally — takes their current JSON output and wraps it.
    /// </summary>
    /// <param name="ctx">Workspace context for populating the <c>context</c> block.</param>
    /// <param name="innerResult">An existing <see cref="CallToolResult"/> whose text content becomes the <c>data</c> value.</param>
    /// <param name="verbose">When <c>true</c>, includes contextual hints in the response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A new <see cref="CallToolResult"/> with the envelope wrapping the inner result's JSON.</returns>
    public static async Task<CallToolResult> WrapAsync(
        WorkspaceContext? ctx,
        CallToolResult innerResult,
        bool verbose,
        CancellationToken ct)
    {
        if (innerResult.IsError == true)
            return innerResult;

        if (innerResult.Content.Count == 0 || innerResult.Content[0] is not TextContentBlock text)
            return innerResult;

        var context = ctx is not null
            ? await BuildContextAsync(ctx, ct)
            : new McpContext(ActiveItemId: null, Workspace: "", CacheAge: "");

        IReadOnlyList<string> hints = verbose && ctx is not null
            ? await McpHintProvider.GetHintsAsync(ctx, ct)
            : [];

        return BuildEnvelopeJsonFromRaw(context, text.Text!, hints);
    }

    /// <summary>
    /// Builds an error envelope with a structured error object.
    /// </summary>
    /// <param name="code">Error code from <see cref="McpErrorCode"/>.</param>
    /// <param name="message">Human-readable error message.</param>
    /// <param name="details">Optional additional details.</param>
    /// <returns>A <see cref="CallToolResult"/> with <c>IsError = true</c>.</returns>
    public static CallToolResult Error(
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteBoolean("success", false);

        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);

        writer.WriteStartObject("details");
        if (details is not null)
        {
            foreach (var (key, value) in details)
                writer.WriteString(key, value);
        }
        writer.WriteEndObject();

        writer.WriteEndObject(); // error

        writer.WriteEndObject(); // root
        writer.Flush();

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(stream.ToArray()) }],
            IsError = true,
        };
    }

    /// <summary>
    /// Builds an error envelope with context information included.
    /// Use when workspace context is available at the point of failure.
    /// </summary>
    public static async Task<CallToolResult> ErrorAsync(
        string code,
        string message,
        WorkspaceContext? ctx,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var context = ctx is not null
            ? await BuildContextAsync(ctx, ct)
            : new McpContext(ActiveItemId: null, Workspace: "", CacheAge: "");

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteBoolean("success", false);

        WriteErrorObject(writer, code, message, details);
        WriteContextObject(writer, context);

        writer.WriteEndObject();
        writer.Flush();

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(stream.ToArray()) }],
            IsError = true,
        };
    }

    /// <summary>
    /// Populates an <see cref="McpContext"/> from the current workspace state.
    /// </summary>
    internal static async Task<McpContext> BuildContextAsync(WorkspaceContext ctx, CancellationToken ct)
    {
        var activeId = await ctx.ContextStore.GetActiveWorkItemIdAsync(ct);
        var workspace = ctx.Key.ToString();

        var cacheAge = "";
        if (activeId.HasValue)
        {
            var item = await ctx.WorkItemRepo.GetByIdAsync(activeId.Value, ct);
            if (item?.LastSyncedAt is not null)
            {
                cacheAge = FormatIsoDuration(DateTimeOffset.UtcNow - item.LastSyncedAt.Value);
            }
        }

        return new McpContext(activeId, workspace, cacheAge);
    }

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as an ISO 8601 duration string (e.g. <c>"PT2M30S"</c>).
    /// </summary>
    internal static string FormatIsoDuration(TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return "PT0S";

        var sb = new StringBuilder("PT");
        var totalHours = (int)elapsed.TotalHours;
        if (totalHours > 0) sb.Append(totalHours).Append('H');
        if (elapsed.Minutes > 0) sb.Append(elapsed.Minutes).Append('M');
        if (elapsed.Seconds > 0 || sb.Length == 2) sb.Append(elapsed.Seconds).Append('S');
        return sb.ToString();
    }

    private static CallToolResult BuildEnvelopeJson(
        McpContext context,
        Action<Utf8JsonWriter> writeData,
        IReadOnlyList<string> hints)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteBoolean("success", true);

        writer.WriteStartObject("data");
        writeData(writer);
        writer.WriteEndObject();

        WriteContextObject(writer, context);
        WriteHintsArray(writer, hints);

        writer.WriteEndObject();
        writer.Flush();

        return McpResultBuilder.ToResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static CallToolResult BuildEnvelopeJsonFromRaw(
        McpContext context,
        string rawDataJson,
        IReadOnlyList<string> hints)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, WriterOptions);

        writer.WriteStartObject();
        writer.WriteBoolean("success", true);

        // Embed the raw JSON as the data object
        writer.WritePropertyName("data");
        using (var doc = JsonDocument.Parse(rawDataJson))
        {
            doc.RootElement.WriteTo(writer);
        }

        WriteContextObject(writer, context);
        WriteHintsArray(writer, hints);

        writer.WriteEndObject();
        writer.Flush();

        return McpResultBuilder.ToResult(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static void WriteContextObject(Utf8JsonWriter writer, McpContext context)
    {
        writer.WriteStartObject("context");

        if (context.ActiveItemId.HasValue)
            writer.WriteNumber("activeItemId", context.ActiveItemId.Value);
        else
            writer.WriteNull("activeItemId");

        writer.WriteString("workspace", context.Workspace);
        writer.WriteString("cacheAge", context.CacheAge);

        writer.WriteEndObject();
    }

    private static void WriteHintsArray(Utf8JsonWriter writer, IReadOnlyList<string> hints)
    {
        writer.WriteStartArray("hints");
        foreach (var hint in hints)
            writer.WriteStringValue(hint);
        writer.WriteEndArray();
    }

    private static void WriteErrorObject(
        Utf8JsonWriter writer,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details)
    {
        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);

        writer.WriteStartObject("details");
        if (details is not null)
        {
            foreach (var (key, value) in details)
                writer.WriteString(key, value);
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
    }
}
