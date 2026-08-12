using System.Text.Json.Serialization;

namespace Twig.Infrastructure.Ado.Dtos;

internal sealed class AdoProcessRuleListResponse
{
    [JsonPropertyName("value")]
    public List<AdoProcessRuleResponse>? Value { get; set; }
}

internal sealed class AdoProcessRuleResponse
{
    [JsonPropertyName("conditions")]
    public List<AdoRuleConditionResponse>? Conditions { get; set; }

    [JsonPropertyName("actions")]
    public List<AdoRuleActionResponse>? Actions { get; set; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; set; }

    /// <summary>
    /// 🔴 <c>custom</c> | <c>inherited</c> | <c>system</c> — where the rule was authored.
    /// </summary>
    /// <remarks>
    /// The only available filter for the ~54 inherited system rules a derived type carries,
    /// and therefore the tag that makes AB#238's carry-everything ruling bearable: the reader
    /// filters a complete document rather than being handed a filtered one.
    /// <para>
    /// 🔴 <b>Nullable, and its absence must NOT be read as any of the three values.</b> Present
    /// at both <c>7.1</c> and <c>7.1-preview.2</c> — verified live 2026-08-12, the two bodies
    /// are byte-identical — but a future version that drops it would otherwise silently
    /// reclassify every authored rule as inherited plumbing, which is exactly the filter a
    /// reader would then use to discard them. See <c>RuleCustomization.Unknown</c>.
    /// </para>
    /// </remarks>
    [JsonPropertyName("customizationType")]
    public string? CustomizationType { get; set; }

    /// <summary>
    /// The rule's display name, e.g. <i>"Epic must state what it delivered"</i>.
    /// </summary>
    /// <remarks>
    /// 🔴 Legitimately <c>null</c> on system plumbing rules — verified live, every one of the
    /// 53 system rules on <c>Niflheim.Epic</c> carries <c>"name": null</c>. Never used as
    /// identity: it is neither unique nor commonly present.
    /// </remarks>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class AdoRuleConditionResponse
{
    [JsonPropertyName("conditionType")]
    public string? ConditionType { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal sealed class AdoRuleActionResponse
{
    [JsonPropertyName("actionType")]
    public string? ActionType { get; set; }

    [JsonPropertyName("targetField")]
    public string? TargetField { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
