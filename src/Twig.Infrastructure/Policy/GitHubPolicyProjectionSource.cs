using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Core;
using Twig.Domain.Services.Policy;
using Twig.Providers.GitHub;
using CorePolicy = Twig.Core.Policy;

namespace Twig.Infrastructure.Policy;

internal sealed record CapturedGitHubPolicy(GitHubRuleProjection Evidence, CorePolicy Policy, bool IsDeclared);

/// <summary>Acquires the actual optional Twig declaration, never external workflow authority.</summary>
internal sealed class GitHubPolicyProjectionSource
{
    private readonly string _path;

    internal GitHubPolicyProjectionSource(string repositoryRoot)
    {
        if (!Path.IsPathFullyQualified(repositoryRoot))
            throw new ArgumentException("An explicit absolute repository root is required.", nameof(repositoryRoot));
        _path = Path.Combine(Path.GetFullPath(repositoryRoot), "twig.policy.json");
    }

    internal async Task<Result<CapturedGitHubPolicy>> CaptureAsync(Connection connection, string actor,
        ProcessSchema nativeProjection, CancellationToken ct)
    {
        if (connection.Provider.Value != "github" || nativeProjection.Connection != connection
            || string.IsNullOrWhiteSpace(actor))
            return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Conflict, "Native policy projection binding is invalid.");
        if (!Directory.Exists(Path.GetDirectoryName(_path)))
            return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable, "The selected repository directory is unavailable.");
        byte[]? bytes;
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous);
            const int limit = 1024 * 1024;
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
            {
                if (buffer.Length + count > limit)
                    return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable, "The Twig policy exceeds the 1 MiB acquisition bound.");
                buffer.Write(chunk, 0, count);
            }
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException) { bytes = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable, "The configured Twig policy could not be acquired.");
        }

        var rules = new List<Rule>();
        if (bytes is not null)
        {
            try
            {
                var json = new UTF8Encoding(false, true).GetString(bytes);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                });
                if (!KnownShape(document.RootElement))
                    return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unsupported,
                        "The Twig declaration contains duplicate or unsupported policy members.");
                var parsed = DeclaredPolicyLoader.Parse(json, _path);
                if (parsed.Failure is { } failure)
                    return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable, failure.Reason);
                foreach (var rule in parsed.Policy!.Rules)
                {
                    var warn = rule.Enforcement == PolicyEnforcement.Warn;
                    if (rule is DependencyIntegrityRule dependency)
                        rules.Add(new(rule.Id, RuleKind.DependencyIntegrity, warnOnly: warn, maxNodes: dependency.MaxNodes));
                    else if (rule is ParentTypeRule parent)
                    {
                        var child = ResolveType(parent.ChildType);
                        var parents = parent.AllowedParentTypes.Select(ResolveType).ToArray();
                        if (child is null || parents.Any(p => p is null))
                            return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable,
                                "A declared policy type is absent or ambiguous in the actual native type catalog.");
                        rules.Add(new(rule.Id, RuleKind.ParentType, child, parents.Select(p => p!), warn));
                    }
                    else return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unsupported, "Unsupported declared Twig policy kind.");
                }
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException or ArgumentException)
            {
                return Result<CapturedGitHubPolicy>.Fail(ErrorCode.Unavailable, "The acquired Twig declaration is invalid.");
            }
        }
        var evidence = new GitHubRuleProjection(connection, actor, nativeProjection.Fingerprint,
            GitHubValidationScope.NativeApiAndDeclaredTwigPolicies, "twig.policy.json/native-api-projection-v1",
            bytes is null ? "absent" : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
            nativeProjection.Types.ToImmutableDictionary(t => t.Reference, _ => ImmutableArray<FieldRule>.Empty));
        return Result<CapturedGitHubPolicy>.Ok(new(evidence, new(rules), bytes is not null));

        string? ResolveType(string value)
        {
            var matches = nativeProjection.Types.Where(t => t.Reference == value || t.Rules.Any(r => r.Disabled
                && r.Values.Any(v => v.DataType == "github.native-type-name" && v.Text == value))).ToArray();
            return matches.Length == 1 ? matches[0].Reference : null;
        }
    }

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
}
