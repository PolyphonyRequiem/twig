using System.Text.Json;
using System.Text.Json.Serialization;

namespace Twig.Domain.Enums;

/// <summary>
/// Base for the profile-vocabulary converters: reads and writes EXACTLY the
/// canonical wire spellings the T1 (AB#732) note declares, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 This is hand-written rather than a <see cref="JsonStringEnumConverter{TEnum}"/>
/// with a naming policy, and the reason is the whole point of the type.
/// <c>JsonStringEnumConverter</c> — with or without a policy — falls back to a
/// case-insensitive <c>Enum.TryParse</c> when a token misses its name cache, so
/// it accepts the CLR member name in addition to the policy spelling. That
/// tolerance is exactly what let the shipped profile drift to
/// <c>"Initiative"</c> / <c>"ParentChild"</c> while the normative schema said
/// <c>"initiative"</c> / <c>"parent-child"</c>, with every test green (AB#735).
/// Verified against the converter, not assumed: a kebab-policy converter was
/// tried first and still parsed <c>"ParentChild"</c>.
/// </para>
/// <para>
/// One concept, one spelling. A profile that uses any other token fails to
/// deserialize, which the provider reports as <c>profile-schema-invalid</c>.
/// </para>
/// </remarks>
internal abstract class CanonicalEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>
    /// Canonical wire token per enum member. Both directions use it.
    /// </summary>
    /// <remarks>
    /// Implementations MUST return a cached instance rather than building one
    /// per call: the converter runs per token, and an expression-bodied property
    /// returning a collection expression allocates a fresh array every read and
    /// every write.
    /// </remarks>
    protected abstract (TEnum Value, string Token)[] Mapping { get; }

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected a string token for {typeof(TEnum).Name}.");

        var token = reader.GetString();
        var mapping = Mapping;
        foreach (var (value, canonical) in mapping)
        {
            // Ordinal: a canonical spelling that only matches case-insensitively
            // is two spellings wearing one name.
            if (string.Equals(token, canonical, StringComparison.Ordinal))
                return value;
        }

        throw new JsonException(
            $"'{token}' is not a canonical {typeof(TEnum).Name} spelling. Expected one of: "
            + string.Join(", ", mapping.Select(m => m.Token)) + ".");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        foreach (var (candidate, canonical) in Mapping)
        {
            if (EqualityComparer<TEnum>.Default.Equals(candidate, value))
            {
                writer.WriteStringValue(canonical);
                return;
            }
        }

        throw new JsonException($"{typeof(TEnum).Name} value '{value}' has no canonical spelling.");
    }
}

/// <summary>
/// <see cref="Role"/> ⇄ the T1 §3 canonical spellings.
/// </summary>
internal sealed class RoleJsonConverter : CanonicalEnumConverter<Role>
{
    private static readonly (Role, string)[] Tokens =
    [
        (Role.Initiative, "initiative"),
        (Role.Investigation, "investigation"),
        (Role.Feature, "feature"),
        (Role.Bug, "bug"),
        (Role.Task, "task"),
    ];

    protected override (Role, string)[] Mapping => Tokens;
}

/// <summary>
/// Canonical <see cref="Role"/> token lookup, for callers that must
/// distinguish "not a vocabulary role" from "malformed document".
/// </summary>
/// <remarks>
/// 🔴 Exists because a strict converter and a named error identifier pull in
/// opposite directions. <see cref="RoleJsonConverter"/> throws on any token
/// outside the five, which the profile loader necessarily reports as
/// <c>profile-schema-invalid</c> — so T1 §6.6's <c>primary-scope-unknown-role</c>
/// would have no path to fire. Deserializing <c>primaryScope.eligibleRoles</c>
/// as raw strings and resolving them HERE keeps the strictness while letting
/// that one field report the specific identifier T1 declares for it.
/// </remarks>
internal static class RoleTokens
{
    private static readonly Dictionary<string, Role> ByToken = new(StringComparer.Ordinal)
    {
        ["initiative"] = Role.Initiative,
        ["investigation"] = Role.Investigation,
        ["feature"] = Role.Feature,
        ["bug"] = Role.Bug,
        ["task"] = Role.Task,
    };

    /// <summary>Resolves a canonical role token. Ordinal — see the converter.</summary>
    public static bool TryResolve(string? token, out Role role)
    {
        if (token is not null) return ByToken.TryGetValue(token, out role);
        role = default;
        return false;
    }
}

/// <summary>
/// <see cref="LinkKind"/> ⇄ the T1 §3.5 canonical spellings.
/// </summary>
internal sealed class LinkKindJsonConverter : CanonicalEnumConverter<LinkKind>
{
    private static readonly (LinkKind, string)[] Tokens =
    [
        (LinkKind.ParentChild, "parent-child"),
        (LinkKind.PredecessorSuccessor, "predecessor-successor"),
        (LinkKind.Related, "related"),
        (LinkKind.Artifact, "artifact"),
    ];

    protected override (LinkKind, string)[] Mapping => Tokens;
}
