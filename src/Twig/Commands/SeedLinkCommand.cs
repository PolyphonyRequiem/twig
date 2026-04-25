using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Formatters;

namespace Twig.Commands;

/// <summary>
/// Handles <c>twig seed link</c>, <c>seed unlink</c>, and <c>seed links</c> commands.
/// </summary>
public sealed class SeedLinkCommand(
    ISeedLinkRepository seedLinkRepo,
    IWorkItemRepository workItemRepo,
    OutputFormatterFactory formatterFactory)
{
    /// <summary>Create a virtual link between two items.</summary>
    public async Task<int> LinkAsync(
        int sourceId,
        int targetId,
        string? type,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        if (sourceId >= 0 && targetId >= 0)
        {
            Console.Error.WriteLine(fmt.FormatError(
                "At least one ID must be a seed (negative). Use ADO for linking positive work items."));
            return 1;
        }

        var rawType = type ?? SeedLinkTypes.Related;
        var linkType = NormalizeLinkType(rawType);
        if (linkType is null)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Invalid link type '{rawType}'. Valid types: {string.Join(", ", SeedLinkTypes.All)}"));
            return 1;
        }

        if (sourceId > 0 && !await workItemRepo.ExistsByIdAsync(sourceId, ct))
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Warning: work item #{sourceId} is not in the local cache. Link created anyway."));
        }
        if (targetId > 0 && !await workItemRepo.ExistsByIdAsync(targetId, ct))
        {
            Console.Error.WriteLine(fmt.FormatInfo(
                $"Warning: work item #{targetId} is not in the local cache. Link created anyway."));
        }

        try
        {
            await seedLinkRepo.AddLinkAsync(new SeedLink(sourceId, targetId, linkType, DateTimeOffset.UtcNow), ct);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // UNIQUE constraint violation — duplicate link
            Console.Error.WriteLine(fmt.FormatError(
                $"A '{linkType}' link from #{sourceId} to #{targetId} already exists."));
            return 1;
        }

        Console.WriteLine(fmt.FormatSuccess(
            $"Linked #{sourceId} ──{linkType}──▶ #{targetId}"));
        return 0;
    }

    /// <summary>Remove a virtual link.</summary>
    public async Task<int> UnlinkAsync(
        int sourceId,
        int targetId,
        string? type,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        var rawType = type ?? SeedLinkTypes.Related;
        var linkType = NormalizeLinkType(rawType);
        if (linkType is null)
        {
            Console.Error.WriteLine(fmt.FormatError(
                $"Invalid link type '{rawType}'. Valid types: {string.Join(", ", SeedLinkTypes.All)}"));
            return 1;
        }

        await seedLinkRepo.RemoveLinkAsync(sourceId, targetId, linkType, ct);

        Console.WriteLine(fmt.FormatSuccess(
            $"Unlinked #{sourceId} ──{linkType}──▶ #{targetId}"));
        return 0;
    }

    private static string? NormalizeLinkType(string type)
    {
        foreach (var t in SeedLinkTypes.All)
            if (string.Equals(t, type, StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }

    /// <summary>List virtual links for an item, or all links.</summary>
    public async Task<int> ListLinksAsync(
        int? id,
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);

        var links = id.HasValue
            ? await seedLinkRepo.GetLinksForItemAsync(id.Value, ct)
            : await seedLinkRepo.GetAllSeedLinksAsync(ct);

        Console.WriteLine(fmt.FormatSeedLinks(links));
        return 0;
    }
}
