using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

/// <summary>
/// Wire shape for <c>GET /_apis/work/processes/{processId}/workItemTypes/{witRefName}/layout</c>.
/// </summary>
/// <remarks>
/// The nesting is pages → sections → groups → controls. Sections are an unlabelled
/// layout concern (the web form's columns) and carry no label of their own, which is
/// why the domain shape flattens them away — see <c>FormLayout</c>.
/// <para>
/// Deliberately partial: <c>extensions</c>, <c>contribution</c> inputs, <c>height</c>,
/// and <c>watermark</c> are not modelled. They describe web rendering, and nothing in
/// twig's terminal presentation can consume them.
/// </para>
/// </remarks>
internal sealed class AdoFormLayoutResponse
{
    [JsonPropertyName("pages")]
    public List<AdoLayoutPageResponse>? Pages { get; set; }

    [JsonPropertyName("systemControls")]
    public List<AdoLayoutControlResponse>? SystemControls { get; set; }
}

internal sealed class AdoLayoutPageResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>One of <c>custom</c>, <c>history</c>, <c>links</c>, <c>attachments</c>.</summary>
    [JsonPropertyName("pageType")]
    public string? PageType { get; set; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("locked")]
    public bool Locked { get; set; }

    [JsonPropertyName("inherited")]
    public bool? Inherited { get; set; }

    [JsonPropertyName("isContribution")]
    public bool IsContribution { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }

    [JsonPropertyName("sections")]
    public List<AdoLayoutSectionResponse>? Sections { get; set; }
}

internal sealed class AdoLayoutSectionResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("groups")]
    public List<AdoLayoutGroupResponse>? Groups { get; set; }
}

internal sealed class AdoLayoutGroupResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("inherited")]
    public bool? Inherited { get; set; }

    [JsonPropertyName("isContribution")]
    public bool IsContribution { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }

    [JsonPropertyName("controls")]
    public List<AdoLayoutControlResponse>? Controls { get; set; }
}

internal sealed class AdoLayoutControlResponse
{
    /// <summary>
    /// For an ordinary field control this is the field reference name
    /// (e.g. <c>Microsoft.VSTS.Common.Priority</c>). For a contribution it is the
    /// contribution id and refers to no field at all.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>
    /// e.g. <c>FieldControl</c>, <c>HtmlFieldControl</c>. Preserved verbatim: the
    /// renderer decides later which kinds have a terminal form, and cannot decide
    /// that if the kind is discarded here.
    /// </summary>
    [JsonPropertyName("controlType")]
    public string? ControlType { get; set; }

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("visible")]
    public bool? Visible { get; set; }

    [JsonPropertyName("inherited")]
    public bool? Inherited { get; set; }

    [JsonPropertyName("isContribution")]
    public bool IsContribution { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }
}
