using Microsoft.Extensions.DependencyInjection;
using Terminal.Gui;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Twig.Domain.Interfaces;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Tui.Views;

SQLitePCL.Batteries.Init();

// Workspace-not-initialized guard — must run before DI container is built
var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");

if (!Directory.Exists(twigDir))
{
    Console.Error.WriteLine("Twig workspace not initialized. Run 'twig init' first.");
    return 1;
}

var configPath = Path.Combine(twigDir, "config");
var config = TwigConfiguration.Load(configPath);

var tempPaths = (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
    ? TwigPaths.ForContext(twigDir, config.Organization, config.Project)
    : new TwigPaths(twigDir, configPath, Path.Combine(twigDir, "twig.db"));

if (!File.Exists(tempPaths.DbPath))
{
    Console.Error.WriteLine("Twig database not found. Run 'twig init' first.");
    return 1;
}

// Build DI container using shared registration
var services = new ServiceCollection();
services.AddTwigCoreServices(config);
var provider = services.BuildServiceProvider();
var workItemRepo = provider.GetRequiredService<IWorkItemRepository>();
var contextStore = provider.GetRequiredService<IContextStore>();
var pendingChangeStore = provider.GetRequiredService<IPendingChangeStore>();
var processConfigProvider = provider.GetRequiredService<IProcessConfigurationProvider>();

// Build icon configuration for badge rendering (same pattern as HumanOutputFormatter)
var iconMode = config.Display.Icons;
var typeIconIds = config.TypeAppearances?
    .Where(a => a.IconId is not null)
    .ToDictionary(a => a.Name, a => a.IconId!, StringComparer.OrdinalIgnoreCase);

using IApplication app = Application.Create();
app.Init();

using var mainWindow = new Window
{
    Title = $"Twig TUI — {config.Organization}/{config.Project} (Esc to quit)",
};

// Create the left-panel tree navigator
var treeNavigator = new TreeNavigatorView(workItemRepo, contextStore, processConfigProvider, iconMode, typeIconIds)
{
    X = 0,
    Y = 0,
    Width = Dim.Percent(40),
    Height = Dim.Fill(),
    Title = "Work Items",
    BorderStyle = LineStyle.Rounded,
};

// Create the right-panel work item form
var formView = new WorkItemFormView(pendingChangeStore)
{
    X = Pos.Right(treeNavigator),
    Y = 0,
    Width = Dim.Fill(),
    Height = Dim.Fill(),
    Title = "Work Item Details",
    BorderStyle = LineStyle.Rounded,
};

// Build the menu bar
var menuBar = new MenuBar(new MenuBarItem[]
{
    new("_File", new View[]
    {
        new MenuItem("_Refresh Tree", "", () =>
        {
            // Blocks UI intentionally — tree reload is fast in practice; async dispatch not yet supported
            Task.Run(async () => await treeNavigator.ReloadAsync()).GetAwaiter().GetResult();
        }),
        new Line(),
        new MenuItem("E_xit", Application.QuitKey, () => mainWindow.App?.RequestStop()),
    }),
    new("_Help", new View[]
    {
        new MenuItem("_About...", "", () =>
        {
            MessageBox.Query(mainWindow.App!, "About Twig TUI", "Twig TUI — Terminal.Gui-based work item navigator\nVim keybindings: j=down, k=up, Enter=select, q=quit", "OK");
        }),
        new MenuItem("_Keybindings", "", () =>
        {
            MessageBox.Query(mainWindow.App!, "Keybindings",
                "j / ↓   Move down\n" +
                "k / ↑   Move up\n" +
                "Enter   Expand / Select\n" +
                "q       Quit\n" +
                "Esc     Close menu / Quit",
                "OK");
        }),
    }),
});

// Wire tree selection → form display
treeNavigator.WorkItemSelected += item =>
{
    formView.LoadWorkItem(item);
};

mainWindow.Add(menuBar, treeNavigator, formView);

// Load the initial tree data
treeNavigator.LoadRootAsync().GetAwaiter().GetResult();

// Set initial focus to the tree navigator so arrow keys work immediately
treeNavigator.SetFocus();

app.Run(mainWindow);

return 0;
