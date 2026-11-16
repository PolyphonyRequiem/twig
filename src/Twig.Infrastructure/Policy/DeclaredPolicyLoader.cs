using System.Text.Json;
using System.Text;
using Twig.Core;

namespace Twig.Infrastructure.Policy;

/// <summary>Outcome of loading <c>twig.policy.json</c>.</summary>
public sealed record DeclaredPolicyLoadResult
{
    /// <summary>The parsed policy, when the file exists and is valid.</summary>
    public DeclaredPolicy? Policy { get; init; }

    /// <summary>A blocking <c>policy-load</c> decision, when the file exists but cannot be used.</summary>
    public Error? Failure { get; init; }
}

/// <summary>
/// Parses the bounded, strict repository-owned policy schema. An unusable declaration
/// is a blocking failure, never a silent permit.
/// </summary>
/// <remarks>
/// Parsed with <see cref="JsonDocument"/> rather than a serializer contract: the schema is small,
/// the reader stays AOT-safe without reflection, and every rejection can name the exact rule and
/// property at fault.
/// </remarks>
public static class DeclaredPolicyLoader
{
    internal const int MaximumBytes = 1024 * 1024;

    /// <summary>Parses policy JSON. <paramref name="source"/> names the file in failure reasons.</summary>
    public static DeclaredPolicyLoadResult Parse(string json, string source = "twig.policy.json")
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            return Fail($"Policy file '{source}' exceeds the 1 MiB acquisition bound.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!KnownShape(document.RootElement))
                return Fail($"Policy file '{source}' contains duplicate or unsupported policy members.");
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
        Failure = new Error(ErrorCode.Unavailable,
            reason + " Declared rules cannot be checked until the file is fixed."),
    };

    private static bool KnownShape(JsonElement root)
    {
        if (!Members(root, ["$schema", "version", "rules"])
            || !root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || !rule.TryGetProperty("kind", out var kind)
                || kind.ValueKind != JsonValueKind.String)
                return false;
            var known = kind.GetString() switch
            {
                DeclaredPolicyRuleKinds.ParentType => Members(rule, ["id", "kind", "enforcement", "childType", "allowedParentTypes"]),
                DeclaredPolicyRuleKinds.DependencyIntegrity => Members(rule, ["id", "kind", "enforcement", "maxNodes"]),
                _ => false,
            };
            if (!known) return false;
        }
        return true;
    }

    private static bool Members(JsonElement value, string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => allowed.Contains(p.Name, StringComparer.Ordinal) && seen.Add(p.Name));
    }

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
            _ => throw new PolicyFormatException(
                $"{where} has unknown kind '{kind}'; supported kinds: {DeclaredPolicyRuleKinds.ParentType}, {DeclaredPolicyRuleKinds.DependencyIntegrity}."),
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
