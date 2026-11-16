/// <summary>Command identities from the same generated declarations that route execution.</summary>
internal static class CommandCatalog
{
    private static readonly Lazy<IReadOnlyList<ProgressiveHelp.CatalogRow>> Catalog = new(() =>
        ProgressiveHelp.ReadCatalog(ProgressiveHelp.CaptureGeneratedHelp(
            Twig.TwigConsoleApp.CreateRunner(_ => { }), ["--help"])));

    internal static IReadOnlyList<ProgressiveHelp.CatalogRow> Rows => Catalog.Value;

    private static readonly Lazy<HashSet<string>> Names = new(() =>
        Rows.SelectMany(row => row.Names).ToHashSet(StringComparer.Ordinal));

    internal static IReadOnlySet<string> KnownCommands => Names.Value;

    internal static bool IsKnownCommand(string[] args) => args.Length > 0
        && (KnownCommands.Contains(args[0]) || SubcommandGuard.IsGroupPrefix(args[0]));
}
