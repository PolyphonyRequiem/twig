using System.Text.Json;

namespace Twig.Infrastructure.Auth;

/// <summary>
/// Single Utf8JsonWriter-based projection of <see cref="IdentityChangeEligibility"/>
/// shared by every consumer (CLI <c>twig connection check</c> JSON output and the MCP
/// <c>twig_connection_check</c> admin tool). One projection means the two surfaces
/// cannot drift: an actor comparing a CLI JSON check against the MCP envelope sees
/// byte-identical semantic data.
/// <para>
/// Hand-written against <see cref="Utf8JsonWriter"/> rather than
/// <see cref="System.Text.Json.JsonSerializer"/> because the twig AOT settings disable
/// reflection-based serialization. Every blocker, every unknown row, every actionable
/// next step is emitted in full — truncating any one of them would hide the exact
/// evidence the caller needs to resolve the switch block.
/// </para>
/// <para>
/// The caller owns the surrounding object: <see cref="WriteBody"/> writes properties
/// into an already-open JSON object (the MCP envelope's <c>data</c> block, or the
/// top-level document the CLI emits). The projection NEVER opens or closes that
/// surrounding object.
/// </para>
/// </summary>
internal static class IdentityChangeEligibilityProjection
{
    /// <summary>
    /// Writes the eligibility snapshot's members into the object the caller has already opened.
    /// Property order is stable so diffs between CLI and MCP output stay reviewable.
    /// </summary>
    internal static void WriteBody(Utf8JsonWriter writer, IdentityChangeEligibility snapshot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(snapshot);

        writer.WriteBoolean("eligible", snapshot.IsEligible);
        writer.WriteString("connectionRef", snapshot.ConnectionRef);
        writer.WriteString("organization", snapshot.Organization);
        writer.WriteString("project", snapshot.Project);
        writer.WriteString("worktreeRoot", snapshot.WorktreeRoot);
        writer.WriteString("worktreeFingerprint", snapshot.WorktreeFingerprint);
        writer.WriteNumber("attachmentRevision", snapshot.AttachmentRevision);

        // Current identity selection travels with the inspector snapshot — the consumer
        // MUST NOT resolve a parallel binding and MUST NOT invent an identity when the
        // snapshot says there isn't one. A missing selection is still a valid blocked
        // inspection payload, not a fabricated authorization.
        WriteOptionalString(writer, "currentBindingId", snapshot.CurrentBindingId);
        WriteOptionalString(writer, "currentIdentityId", snapshot.CurrentIdentityId);
        WriteOptionalString(writer, "currentIdentityName", snapshot.CurrentIdentityName);
        WriteOptionalLong(writer, "selectionRevision", snapshot.SelectionRevision);
        WriteOptionalString(writer, "selectionSource", snapshot.SelectionSource);

        writer.WriteStartArray("pendingEdits");
        foreach (var p in snapshot.PendingEdits)
        {
            writer.WriteStartObject();
            writer.WriteNumber("pendingChangeId", p.PendingChangeId);
            writer.WriteNumber("workItemId", p.WorkItemId);
            writer.WriteString("kind", p.Kind);
            writer.WriteBoolean("isSeed", p.IsSeed);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("localSeeds");
        foreach (var l in snapshot.LocalSeeds)
        {
            writer.WriteStartObject();
            writer.WriteNumber("seedAlias", l.SeedAlias);
            writer.WriteString("title", l.Title);
            writer.WriteString("typeName", l.TypeName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("openPublishIntents");
        foreach (var i in snapshot.OpenPublishIntents)
        {
            writer.WriteStartObject();
            writer.WriteString("identity", i.Identity);
            writer.WriteString("title", i.Title);
            writer.WriteString("typeName", i.TypeName);
            writer.WriteString("recordedAt", i.RecordedAt);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("unresolvedJournals");
        foreach (var j in snapshot.UnresolvedJournals)
        {
            writer.WriteStartObject();
            writer.WriteString("digest", j.Digest);
            writer.WriteString("opId", j.OpId);
            writer.WriteString("sourcePath", j.SourcePath);
            writer.WriteString("state", j.State.ToString());
            writer.WriteString("reason", j.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("reservedClaims");
        foreach (var c in snapshot.ReservedClaims)
        {
            writer.WriteStartObject();
            writer.WriteString("claimId", c.ClaimId);
            writer.WriteString("mintedAt", c.MintedAt);
            writer.WriteString("observedState", c.ObservedState);
            writer.WriteNumber("workItemId", c.WorkItemId);
            writer.WriteString("primaryScopeKind", c.PrimaryScopeKind);
            writer.WriteString("casToken", c.CasToken);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("worktreeGuards");
        foreach (var g in snapshot.WorktreeGuards)
        {
            writer.WriteStartObject();
            writer.WriteString("guard", g.Guard);
            writer.WriteString("observed", g.Observed);
            writer.WriteString("expected", g.Expected);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("unknownRows");
        foreach (var u in snapshot.UnknownRows)
        {
            writer.WriteStartObject();
            writer.WriteString("source", u.Source);
            writer.WriteString("detail", u.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartArray("actionableNextSteps");
        foreach (var step in snapshot.ActionableNextSteps)
            writer.WriteStringValue(step);
        writer.WriteEndArray();
    }

    /// <summary>Serializes the snapshot as a standalone top-level JSON object. Used by the CLI JSON output path.</summary>
    internal static void WriteDocument(Utf8JsonWriter writer, IdentityChangeEligibility snapshot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        WriteBody(writer, snapshot);
        writer.WriteEndObject();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    private static void WriteOptionalLong(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteNumber(name, value.Value);
    }
}
