using NSubstitute;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Formatters;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Shared setup for SaveCommand test classes: mocked dependencies, formatter factory,
/// active item resolver, and a <see cref="CreateCommand"/> factory.
/// </summary>
public abstract class SaveCommandTestBase
{
    protected readonly IContextStore _contextStore;
    protected readonly IWorkItemRepository _workItemRepo;
    protected readonly IAdoWorkItemService _adoService;
    protected readonly IPendingChangeStore _pendingChangeStore;
    protected readonly IConsoleInput _consoleInput;
    protected readonly OutputFormatterFactory _formatterFactory;
    protected readonly ActiveItemResolver _resolver;

    protected SaveCommandTestBase()
    {
        _contextStore = Substitute.For<IContextStore>();
        _workItemRepo = Substitute.For<IWorkItemRepository>();
        _adoService = Substitute.For<IAdoWorkItemService>();
        _pendingChangeStore = Substitute.For<IPendingChangeStore>();
        _consoleInput = Substitute.For<IConsoleInput>();
        _formatterFactory = new OutputFormatterFactory(
            new HumanOutputFormatter(), new JsonOutputFormatter(),
            new JsonCompactOutputFormatter(new JsonOutputFormatter()), new MinimalOutputFormatter());
        _resolver = new ActiveItemResolver(_contextStore, _workItemRepo, _adoService);
    }

    protected SaveCommand CreateCommand(TextWriter? stderr = null)
    {
        var flusher = new PendingChangeFlusher(_workItemRepo, _adoService, _pendingChangeStore, _consoleInput, _formatterFactory, stderr);
        return new(_workItemRepo, _pendingChangeStore, flusher, _resolver, _formatterFactory, stderr: stderr);
    }
}
