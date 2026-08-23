using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Plan;

/// <summary>
/// Deterministically hashes a staged seed. The plan-apply pass recomputes this over the
/// current cache and refuses a <c>publish-seed</c> operation whose expected fingerprint no
/// longer matches — that is the drift detector that keeps a plan bound to the exact seed
/// shape the author saw.
/// <para>
/// Byte-shape is fixed and public in behaviour, not in surface. Order-sensitive fields go
/// in a fixed layout; the free-form <c>Fields</c> dictionary is emitted with property names
/// sorted by ordinal ascending; seed links are emitted sorted by (source, target, linkType).
/// A caller cannot end up with two different fingerprints for two seeds that have the
/// same visible content simply because one was hydrated in a different order.
/// </para>
/// <para>
/// Every endpoint that could name a staged neighbour — <c>ParentId</c>, and every seed link
/// source/target — is emitted as an <b>identity token</b> so a peer publishing between plan
/// and apply cannot perturb the hash:
/// </para>
/// <list type="bullet">
///   <item><c>seed:&lt;stagedIdentity&gt;</c> when the endpoint resolves to a registered
///     staged identity — whether via its negative alias (looked up in the register) or via
///     the positive ADO id it has already published as (looked up in the publish map).</item>
///   <item><c>ado:&lt;id&gt;</c> when the endpoint is a positive id that has never been
///     published by any staged seed (a true real ADO item), or a negative alias that was
///     never registered.</item>
/// </list>
/// <para>
/// Ambiguity in the publish map — a single ADO id claimed by two identities — is a
/// determinate corruption: the calculator throws rather than picking silently.
/// </para>
/// </summary>
internal static class SeedFingerprintCalculator
{
    /// <summary>
    /// Computes the canonical seed fingerprint. Returns the lowercase-hex SHA-256 of the
    /// canonical UTF-8 bytes. <paramref name="links"/> is copied and sorted internally;
    /// callers do not need to pre-sort.
    /// </summary>
    public static async Task<string> ComputeAsync(
        WorkItem seed,
        IReadOnlyList<SeedLink> links,
        IStagedIdentityRegistry stagedRegistry,
        IPublishIdMapRepository publishIdMap,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(stagedRegistry);
        ArgumentNullException.ThrowIfNull(publishIdMap);

        var canonicalizer = await SeedEndpointCanonicalizer
            .CreateAsync(stagedRegistry, publishIdMap, ct)
            .ConfigureAwait(false);
        var bytes = await CanonicalizeToUtf8Async(seed, links, canonicalizer, ct).ConfigureAwait(false);
        return PlanCanonicalizer.ComputeDigest(bytes);
    }

    private static async Task<byte[]> CanonicalizeToUtf8Async(
        WorkItem seed,
        IReadOnlyList<SeedLink> links,
        SeedEndpointCanonicalizer canonicalizer,
        CancellationToken ct)
    {
        // Resolve every endpoint token BEFORE writing, so the writer stays synchronous and
        // the JSON layout stays word-for-word deterministic under any I/O ordering.
        var parentToken = seed.ParentId.HasValue
            ? await canonicalizer.CanonicalizeAsync(seed.ParentId.Value, ct).ConfigureAwait(false)
            : null;

        var linkTokens = new (string Source, string Target, string LinkType)[links.Count];
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var src = await canonicalizer.CanonicalizeAsync(link.SourceId, ct).ConfigureAwait(false);
            var tgt = await canonicalizer.CanonicalizeAsync(link.TargetId, ct).ConfigureAwait(false);
            linkTokens[i] = (src, tgt, link.LinkType);
        }
        Array.Sort(linkTokens, static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Source, b.Source);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Target, b.Target);
            if (c != 0) return c;
            return string.CompareOrdinal(a.LinkType, b.LinkType);
        });

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();

            writer.WriteString("assignedTo", seed.AssignedTo);
            writer.WriteString("areaPath", seed.AreaPath.Value);

            // Fields — sorted by key so hydration order does not perturb the hash.
            writer.WriteStartObject("fields");
            var sortedFields = new SortedDictionary<string, string?>(
                (IDictionary<string, string?>)seed.Fields,
                StringComparer.Ordinal);
            foreach (var kv in sortedFields)
                writer.WriteString(kv.Key, kv.Value);
            writer.WriteEndObject();

            writer.WriteString("iterationPath", seed.IterationPath.Value);

            // Seed links — endpoints are identity tokens, sorted by (source, target, linkType).
            writer.WriteStartArray("links");
            foreach (var link in linkTokens)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceId", link.Source);
                writer.WriteString("targetId", link.Target);
                writer.WriteString("linkType", link.LinkType);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (parentToken is null)
                writer.WriteNull("parentId");
            else
                writer.WriteString("parentId", parentToken);

            writer.WriteString(
                "stagedIdentity",
                seed.StagedIdentity is { } id ? id.ToString() : string.Empty);
            writer.WriteString("title", seed.Title);
            writer.WriteString("type", seed.Type.Value);

            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

/// <summary>
/// Resolves a seed-link or parent endpoint (a raw integer id from the local cache) to a
/// stable identity token — <c>seed:&lt;identity&gt;</c> for anything that traces back to a
/// registered staged seed, <c>ado:&lt;id&gt;</c> otherwise. Pre-loads the publish map once
/// so every endpoint on a single seed is resolved against the same snapshot.
/// </summary>
internal sealed class SeedEndpointCanonicalizer
{
    private readonly IStagedIdentityRegistry _registry;
    private readonly IReadOnlyDictionary<int, StagedIdentity> _newIdToIdentity;

    private SeedEndpointCanonicalizer(
        IStagedIdentityRegistry registry,
        IReadOnlyDictionary<int, StagedIdentity> newIdToIdentity)
    {
        _registry = registry;
        _newIdToIdentity = newIdToIdentity;
    }

    /// <summary>
    /// Snapshots the publish map into a positive-id → identity lookup. Duplicate rows for
    /// the same ADO id must agree on identity; a contradiction throws because a silent
    /// pick would let the fingerprint drift with which row wins the coin flip.
    /// </summary>
    public static async Task<SeedEndpointCanonicalizer> CreateAsync(
        IStagedIdentityRegistry registry,
        IPublishIdMapRepository publishIdMap,
        CancellationToken ct)
    {
        var mappings = await publishIdMap.GetAllMappingsAsync(ct).ConfigureAwait(false);
        var byNewId = new Dictionary<int, StagedIdentity>(mappings.Count);
        foreach (var m in mappings)
        {
            if (byNewId.TryGetValue(m.NewId, out var existing))
            {
                if (existing != m.Identity)
                    throw new InvalidOperationException(
                        $"Ambiguous publish mapping: ADO id {m.NewId} is claimed by both " +
                        $"identity {existing} and identity {m.Identity}. Refuse to canonicalize " +
                        "a seed fingerprint over a corrupted publish map.");
            }
            else
            {
                byNewId[m.NewId] = m.Identity;
            }
        }
        return new SeedEndpointCanonicalizer(registry, byNewId);
    }

    /// <summary>
    /// Returns <c>seed:&lt;identity&gt;</c> when <paramref name="endpoint"/> traces back to
    /// a registered staged identity, <c>ado:&lt;id&gt;</c> otherwise.
    /// </summary>
    public async Task<string> CanonicalizeAsync(int endpoint, CancellationToken ct)
    {
        if (endpoint < 0)
        {
            // Negative endpoints are staged aliases; the register is authoritative.
            if (StagedAlias.TryFrom(endpoint, out var alias))
            {
                var identity = await _registry.FindByAliasAsync(alias, ct).ConfigureAwait(false);
                if (identity is { } id)
                    return "seed:" + id.ToString();
            }
            // Unregistered negative id (a stale link, a legacy row) — fall back to the raw
            // form so the endpoint still contributes deterministically.
            return "ado:" + endpoint.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return _newIdToIdentity.TryGetValue(endpoint, out var mapped)
            ? "seed:" + mapped.ToString()
            : "ado:" + endpoint.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
