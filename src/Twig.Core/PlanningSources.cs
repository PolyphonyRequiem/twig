using System.Collections.Immutable;

namespace Twig.Core;

public enum PlanningSourceKind { SavedQuery, Backlog }
public sealed record PlanningSource(PlanningSourceKind Kind, string Id, string Name, string Path,
    bool IsFolder = false, string? QueryType = null, bool RequiresTeam = false);
public sealed record PlanningSources(string? Team, ImmutableArray<PlanningSource> Entries, ImmutableArray<string> Gaps);
public sealed record PlanningBrowsePage(PlanningSource Source, QueryPage Page, DateTimeOffset ObservedAt, string? Team = null);
public sealed record PlanningBrowse(PlanningSource Source, ImmutableArray<ItemKey> Items, DateTimeOffset ObservedAt, string? Team = null);
public sealed record PlanningMembership(PlanningSourceKind Kind, string Id, string? Team,
    ImmutableArray<ItemKey> Items, DateTimeOffset ObservedAt, bool Complete);

public static partial class Core
{
    public static IEnumerable<Query> PlanningReferences(Query query)
    {
        if (query.Kind is QueryKind.SavedQuery or QueryKind.Backlog) yield return query;
        foreach (var child in query.Children)
            foreach (var reference in PlanningReferences(child)) yield return reference;
    }

    public static Result<PlanningMembership> PlanningMembershipFor(Query reference, IEnumerable<PlanningMembership> memberships)
    {
        var kind = reference.Kind switch
        {
            QueryKind.SavedQuery => PlanningSourceKind.SavedQuery,
            QueryKind.Backlog => PlanningSourceKind.Backlog,
            _ => throw new ArgumentException("A saved-query or backlog reference is required."),
        };
        var matches = memberships.Where(value => value.Kind == kind && value.Id == reference.Value!.Text
            && value.Team == reference.PlanningTeam).ToArray();
        return matches.Length == 1 && matches[0].Complete && matches[0].ObservedAt != default && !matches[0].Items.IsDefault
            ? Result<PlanningMembership>.Ok(matches[0])
            : Result<PlanningMembership>.Fail(ErrorCode.Unavailable, "This exact saved-query/backlog and team have no complete cached membership. Refresh that native source explicitly; another source is not a substitute.");
    }
}
