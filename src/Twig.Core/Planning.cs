using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Twig.Core;

public sealed record PlanningTeam(string Id, string Name);
public sealed record PlanningIteration(string Path, DateTimeOffset? Start, DateTimeOffset? Finish, bool SelectedTeam = true);
public sealed record PlanningMetadata(DateTimeOffset ObservedAt, ImmutableArray<string> Areas,
    ImmutableArray<PlanningIteration> Iterations, ImmutableArray<string> Gaps, string? Team = null);
public sealed record PlanningCacheEntry(PlanningMetadata? Metadata);

public sealed record IterationExpression
{
    public IterationExpression(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim() || text.Any(char.IsControl))
            throw new ArgumentException("Use an absolute iteration path or @Current with an optional nonzero signed offset.");
        if (!text.StartsWith('@')) { Text = text; return; }
        var match = Regex.Match(text, @"^@current(?:([+-])([1-9][0-9]*))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success || match.Groups[2].Success && !int.TryParse(match.Groups[2].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException("Use @Current, @Current+N or @Current-N with a positive integer N; zero and unknown expressions are invalid.");
        IsRelative = true;
        var magnitude = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        Offset = match.Groups[1].Value == "-" ? -magnitude : magnitude;
        Text = "@Current" + (Offset == 0 ? "" : (Offset > 0 ? "+" : "") + Offset.ToString(CultureInfo.InvariantCulture));
    }

    public string Text { get; }
    public bool IsRelative { get; }
    public int Offset { get; }
}

public static partial class Core
{
    public static bool IsEmptyQuery(Query query) => query.Kind switch
    {
        QueryKind.Iteration => query.Value!.Values.IsEmpty,
        QueryKind.Not => query.Children[0].Kind == QueryKind.All,
        QueryKind.And => query.Children.Any(IsEmptyQuery),
        QueryKind.Or => query.Children.All(IsEmptyQuery),
        _ => false,
    };

    public static Result<Query> ResolvePlanningQuery(Query query, PlanningMetadata? planning, DateTimeOffset? now = null)
        => ResolvePlanningQuery(query, planning, now, null);

    public static Result<Query> ResolvePlanningQuery(Query query, PlanningMetadata? planning, DateTimeOffset? now,
        IEnumerable<PlanningMetadata>? planningCalendars)
    {
        if (query.Kind == QueryKind.Iteration)
        {
            if (planningCalendars is not null && query.Value!.Values.Any(text => new IterationExpression(text).IsRelative))
            {
                var calendars = planningCalendars.Where(value => value.Team == query.PlanningTeam).ToArray();
                if (query.PlanningTeam is null || calendars.Length != 1)
                    return Result<Query>.Fail(ErrorCode.Unavailable, "This exact native team has no unique retained calendar; another team's calendar is not a substitute.");
                planning = calendars[0];
            }
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in query.Value!.Values)
            {
                var expression = new IterationExpression(text);
                if (!expression.IsRelative) { paths.Add(expression.Text); continue; }
                if (planning is null || query.PlanningTeam is null || planning.Team != query.PlanningTeam)
                    return Result<Query>.Fail(ErrorCode.Unavailable, "Relative iteration pins require the explicitly selected native team's refreshed calendar; another team's calendar is not a substitute.");
                if (now is not { } instant || planning.ObservedAt > instant
                    || instant - planning.ObservedAt > TimeSpan.FromHours(24) || !planning.Gaps.IsEmpty)
                    return Result<Query>.Fail(ErrorCode.Unavailable, "Relative iteration membership needs a current, complete team calendar.");
                var selected = planning.Iterations.Where(i => i.SelectedTeam).OrderBy(i => i.Start).ToArray();
                if (selected.Any(i => i.Start is null || i.Finish is null))
                    return Result<Query>.Fail(ErrorCode.Unavailable, "The selected team calendar has an undated iteration.");
                var intervals = selected.Select(i => (Start: i.Start!.Value.UtcDateTime.Date,
                    Finish: i.Finish!.Value.UtcDateTime.Date, i.Path)).ToArray();
                if (intervals.Any(i => i.Start > i.Finish) || intervals.Zip(intervals.Skip(1))
                    .Any(pair => pair.First.Finish >= pair.Second.Start))
                    return Result<Query>.Fail(ErrorCode.Unavailable, "The selected team calendar has ambiguous dates.");
                var day = instant.UtcDateTime.Date;
                var current = Array.FindIndex(intervals, i => i.Start <= day && day <= i.Finish);
                var target = (long)current + expression.Offset;
                if (current < 0 || target < 0 || target >= intervals.Length)
                    return Result<Query>.Fail(ErrorCode.Unavailable, "The selected team calendar does not cover the requested offset.");
                for (var i = Math.Min(current, (int)target); i < Math.Max(current, (int)target); i++)
                    if ((intervals[i + 1].Start - intervals[i].Finish).Days != 1)
                        return Result<Query>.Fail(ErrorCode.Unavailable, "The selected team calendar has a gap in the requested offset.");
                paths.Add(intervals[target].Path);
            }
            var alternatives = paths.Order(StringComparer.OrdinalIgnoreCase)
                .Select(path => new Query(QueryKind.PathEqual, query.Field, FieldValue.String(path))).ToArray();
            return Result<Query>.Ok(alternatives.Length == 0 ? new(QueryKind.Not, children: [new(QueryKind.All)])
                : alternatives.Length == 1 ? alternatives[0] : new(QueryKind.Or, children: alternatives));
        }
        if (query.Children.IsEmpty) return Result<Query>.Ok(query);
        var children = new List<Query>();
        foreach (var child in query.Children)
        {
            var resolved = ResolvePlanningQuery(child, planning, now, planningCalendars);
            if (!resolved.IsSuccess) return resolved;
            children.Add(resolved.Value);
        }
        return Result<Query>.Ok(new(query.Kind, children: children));
    }
}
