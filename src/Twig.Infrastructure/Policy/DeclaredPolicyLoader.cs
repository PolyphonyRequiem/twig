using System.Text.Json;
using Twig.Domain.Services.Policy;

namespace Twig.Infrastructure.Policy;

/// <summary>Outcome of loading <c>twig.policy.json</c>.</summary>
public sealed record DeclaredPolicyLoadResult
{
    /// <summary>No policy file: no declared rules.</summary>
    public static readonly DeclaredPolicyLoadResult None = new();

    /// <summary>The parsed policy, when the file exists and is valid.</summary>
    public DeclaredPolicy? Policy { get; init; }

    /// <summary>A blocking <c>policy-load</c> decision, when the file exists but cannot be used.</summary>
    public PolicyDecision? Failure { get; init; }
}

/// <summary>
/// Reads the repository-owned declared policy (AB#1169, D2). A present but unusable file is a
/// blocking <see cref="PolicyOutcome.Unavailable"/> decision, never a silent permit (D3).
/// </summary>
/// <remarks>
/// Parsed with <see cref="JsonDocument"/> rather than a serializer contract: the schema is small,
/// the reader stays AOT-safe without reflection, and every rejection can name the exact rule and
/// property at fault.
/// </remarks>
public sealed class DeclaredPolicyLoader(string policyPath)
{
    /// <summary>The file this loader reads.</summary>
    public string PolicyPath { get; } = policyPath;

    /// <summary>Loads the policy file, if any.</summary>
    public async Task<DeclaredPolicyLoadResult> LoadAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await File.ReadAllTextAsync(PolicyPath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return DeclaredPolicyLoadResult.None;
        }
        catch (DirectoryNotFoundException)
        {
            return DeclaredPolicyLoadResult.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Fail($"Policy file '{PolicyPath}' could not be read: {ex.Message}");
        }

        return Parse(json, PolicyPath);
    }

    /// <summary>Parses policy JSON. <paramref name="source"/> names the file in failure reasons.</summary>
    public static DeclaredPolicyLoadResult Parse(string json, string source = "twig.policy.json")
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return new DeclaredPolicyLoadResult { Policy = ReadPolicy(document.RootElement) };
        }
        catch (JsonException ex)
        {
            return Fail($"Policy file '{source}' is not valid JSON: {ex.Message}");
        }
        catch (PolicyFormatException ex)
        {
            return Fail($"Policy file '{source}' is invalid: {ex.Message}");
        }
    }

    private static DeclaredPolicyLoadResult Fail(string reason) => new()
    {
        Failure = new PolicyDecision
        {
            RuleId = DeclaredPolicyRuleKinds.PolicyLoad,
            Kind = DeclaredPolicyRuleKinds.PolicyLoad,
            Outcome = PolicyOutcome.Unavailable,
            Reason = reason + " Declared rules cannot be checked until the file is fixed.",
        },
    };

    private static DeclaredPolicy ReadPolicy(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PolicyFormatException("the root must be a JSON object.");

        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v))
        {
            throw new PolicyFormatException("'version' is required and must be an integer.");
        }

        if (v != DeclaredPolicy.SupportedVersion)
            throw new PolicyFormatException($"version {v} is not supported; this Twig build understands version {DeclaredPolicy.SupportedVersion}.");

        if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            throw new PolicyFormatException("'rules' is required and must be an array.");

        var parsed = new List<DeclaredPolicyRule>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in rules.EnumerateArray())
        {
            var rule = ReadRule(element, index++);
            if (!ids.Add(rule.Id))
                throw new PolicyFormatException($"rule id '{rule.Id}' is declared more than once.");
            parsed.Add(rule);
        }

        return new DeclaredPolicy { Rules = parsed };
    }

    private static DeclaredPolicyRule ReadRule(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PolicyFormatException($"rules[{index}] must be an object.");

        var id = RequiredString(element, "id", $"rules[{index}]");
        var where = $"rule '{id}'";
        var kind = RequiredString(element, "kind", where);
        var enforcement = ReadEnforcement(element, where);

        DeclaredPolicyRule rule = kind switch
        {
            DeclaredPolicyRuleKinds.ParentType => new ParentTypeRule
            {
                Id = id,
                ChildType = RequiredString(element, "childType", where),
                AllowedParentTypes = RequiredStrings(element, "allowedParentTypes", where),
            },
            DeclaredPolicyRuleKinds.DependencyIntegrity => new DependencyIntegrityRule
            {
                Id = id,
                MaxNodes = OptionalPositiveInt(element, "maxNodes", where) ?? DependencyIntegrityRule.DefaultMaxNodes,
            },
            DeclaredPolicyRuleKinds.LabelApplicability => new LabelApplicabilityRule
            {
                Id = id,
                Prefix = RequiredString(element, "prefix", where),
                AllowedTypes = RequiredStrings(element, "allowedTypes", where),
                Exclusive = OptionalBool(element, "exclusive", where) ?? false,
                Field = OptionalString(element, "field", where) ?? LabelApplicabilityRule.DefaultField,
            },
            _ => throw new PolicyFormatException(
                $"{where} has unknown kind '{kind}'; supported kinds: {DeclaredPolicyRuleKinds.ParentType}, {DeclaredPolicyRuleKinds.DependencyIntegrity}, {DeclaredPolicyRuleKinds.LabelApplicability}."),
        };

        return rule with { Enforcement = enforcement };
    }

    private static PolicyEnforcement ReadEnforcement(JsonElement element, string where) =>
        OptionalString(element, "enforcement", where) switch
        {
            null => PolicyEnforcement.Refuse,
            var s when s.Equals("refuse", StringComparison.OrdinalIgnoreCase) => PolicyEnforcement.Refuse,
            var s when s.Equals("warn", StringComparison.OrdinalIgnoreCase) => PolicyEnforcement.Warn,
            var s => throw new PolicyFormatException($"{where} has enforcement '{s}'; expected 'refuse' or 'warn'."),
        };

    private static string RequiredString(JsonElement element, string name, string where) =>
        OptionalString(element, name, where) ?? throw new PolicyFormatException($"{where} requires '{name}'.");

    private static string? OptionalString(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new PolicyFormatException($"{where} property '{name}' must be a non-empty string.");
        return value.GetString()!.Trim();
    }

    private static IReadOnlyList<string> RequiredStrings(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new PolicyFormatException($"{where} requires '{name}' as an array of strings.");

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new PolicyFormatException($"{where} property '{name}' must contain only non-empty strings.");
            items.Add(item.GetString()!.Trim());
        }

        if (items.Count == 0)
            throw new PolicyFormatException($"{where} property '{name}' must not be empty.");
        return items;
    }

    private static bool? OptionalBool(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new PolicyFormatException($"{where} property '{name}' must be true or false."),
        };
    }

    private static int? OptionalPositiveInt(JsonElement element, string name, string where)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n <= 0)
            throw new PolicyFormatException($"{where} property '{name}' must be a positive integer.");
        return n;
    }

    private sealed class PolicyFormatException(string message) : Exception(message);
}
