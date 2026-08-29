namespace Twig.Domain.ValueObjects;

/// <summary>
/// Cached metadata for an ADO work item field — sourced from the
/// <c>GET /{project}/_apis/wit/fields</c> endpoint or derived from the reference name.
/// </summary>
/// <param name="ReferenceName">The field's stable reference name.</param>
/// <param name="DisplayName">The field's human-facing name.</param>
/// <param name="DataType">ADO's declared data type (<c>string</c>, <c>html</c>, …).</param>
/// <param name="IsReadOnly">Whether ADO refuses writes to this field.</param>
public sealed record FieldDefinition(
    string ReferenceName,
    string DisplayName,
    string DataType,
    bool IsReadOnly)
{
    /// <summary>
    /// 🔴 Whether ADO treats this field as an <b>identity</b> field, read from the fields
    /// route's <c>isIdentity</c> attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is NOT derivable from <see cref="DataType"/>: ADO reports identity fields as
    /// <c>string</c>, so the two are indistinguishable without this flag — which is why
    /// AB#802's readback could not tell an identity from an ordinary string and compared it
    /// byte-for-byte, reporting every landed identity write as <c>Indeterminate</c>.
    /// </para>
    /// <para>
    /// An <c>init</c> property rather than a positional parameter on purpose: positional
    /// would rewrite the record's primary constructor and <c>Deconstruct</c>, breaking a
    /// shipped public signature for a field every existing caller is happy to default.
    /// </para>
    /// <para>
    /// The server omits the attribute rather than sending <c>false</c>, so absent becomes
    /// <c>false</c>. That default is fail-closed for every consumer: a field not recognised
    /// as an identity keeps ordinal comparison — the pre-AB#802 behaviour — never a laxer
    /// one.
    /// </para>
    /// </remarks>
    public bool IsIdentity { get; init; }
}
