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
