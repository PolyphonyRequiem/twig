using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Formatters;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.DependencyInjection;

/// <summary>
/// Wayfinder ticket 0008 — CLI registration completeness.
///
/// A CLI capability only works when THREE things line up: the handler method on
/// <see cref="TwigCommands"/>, the DI registration in
/// <see cref="CommandRegistrationModule"/>, and the entry in
/// <c>GroupedHelp.KnownCommands</c>. Only the third had a guard; a missing DI
/// registration failed at RUNTIME with an opaque
/// <c>InvalidOperationException: No service for type ... has been registered</c>,
/// and a partially-satisfied constructor failed even more quietly by silently
/// selecting a degraded overload.
///
/// These tests close both gaps at build time.
/// </summary>
public sealed class CommandRegistrationCompletenessTests
{
    /// <summary>
    /// Parameter types that are deliberately NOT container-resolved: primitives,
    /// strings, and console plumbing that defaults to <c>Console.Error</c>.
    /// </summary>
    private static readonly HashSet<Type> NonInjectedParameterTypes =
    [
        typeof(TextWriter),
        typeof(TextReader),
        typeof(string),
        typeof(bool),
        typeof(int),
        typeof(CancellationToken),
    ];

    private static ServiceCollection BuildCommandServices()
    {
        var services = new ServiceCollection();

        // Domain interfaces
        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IPendingChangeStore>());
        services.AddSingleton(Substitute.For<IPendingChangeFlusher>());
        services.AddSingleton(Substitute.For<IProcessConfigurationProvider>());
        services.AddSingleton(Substitute.For<IProcessTypeStore>());
        services.AddSingleton(Substitute.For<IFieldDefinitionStore>());
        services.AddSingleton(Substitute.For<ISeedLinkRepository>());
        services.AddSingleton(Substitute.For<IPublishIdMapRepository>());
        services.AddSingleton(Substitute.For<IPublishIntentRepository>());
        services.AddSingleton(Substitute.For<ISeedPublishRulesProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWork>());
        services.AddSingleton(Substitute.For<IConsoleInput>());
        services.AddSingleton(Substitute.For<IWorkItemLinkRepository>());
        services.AddSingleton(Substitute.For<IPromptStateWriter>());
        services.AddSingleton(Substitute.For<INavigationHistoryStore>());
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton(Substitute.For<IFormLayoutProvider>());
        services.AddSingleton(Substitute.For<ITrackingRepository>());
        services.AddSingleton(Substitute.For<ITrackingService>());
        services.AddSingleton(Substitute.For<ISprintHierarchyBuilder>());
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());
        services.AddSingleton(Substitute.For<IGlobalProfileStore>());
        services.AddSingleton(Substitute.For<IAdoGitService>());
        services.AddSingleton(Substitute.For<ITelemetryClient>());
        services.AddSingleton<SprintIterationResolver>();

        // Domain services that production registers via
        // TwigServiceRegistration.AddConnectionServices. Commands take these as
        // optional constructor parameters, so omitting them here would silently
        // exercise the degraded overloads this test exists to catch.
        services.AddSingleton(Substitute.For<IStagedIdentityRegistry>());
        services.AddSingleton<Twig.Domain.Services.Seed.SeedFactory>();
        services.AddSingleton<Twig.Domain.Services.Seed.SeedDiscardOrchestrator>();

        services.AddSingleton(new HttpClient());
        services.AddSingleton(new OutputFormatterFactory(new HumanOutputFormatter()));
        services.AddSingleton(new TwigConfiguration
        {
            Display = new DisplayConfig { CacheStaleMinutes = 30 },
            User = new UserConfig { DisplayName = "Test User" },
        });
        services.AddSingleton(new TwigPaths(
            Path.Combine(Path.GetTempPath(), ".twig-test"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "config"),
            Path.Combine(Path.GetTempPath(), ".twig-test", "twig.db")));

        services.AddSingleton(Substitute.For<IAsyncRenderer>());
        services.AddSingleton<RenderingPipelineFactory>();
        services.AddSingleton<RendererFactory>();

        // Surface-neutral domain services moved to Infrastructure (wayfinder 0016);
        // compose both seams rather than re-listing either.
        services.AddConnectionDomainServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();

        return services;
    }

    /// <summary>
    /// Touch points 4 + 5: every command type a <see cref="TwigCommands"/> or
    /// <see cref="OhMyPoshCommands"/> handler resolves out of the container must
    /// actually be registered by <see cref="CommandRegistrationModule"/>.
    ///
    /// The handler bodies call <c>services.GetRequiredService&lt;T&gt;()</c>, which
    /// reflection cannot see, so the demanded types are read from the dispatcher
    /// source. That source IS the wiring contract; reading it is the point.
    /// </summary>
    [Fact]
    public void EveryCommandResolvedByADispatcherHandler_IsRegistered()
    {
        var demanded = ReadDispatcherDemandedCommandTypes();

        // Sanity: if the scrape ever silently matches nothing, this test would
        // vacuously pass. Anchor it to the real order of magnitude.
        demanded.Count.ShouldBeGreaterThan(30,
            "Dispatcher scrape found almost nothing — the source layout changed and this "
            + "guard has gone vacuous. Fix the scrape, do not delete the assertion.");

        var registered = BuildCommandServices()
            .Select(descriptor => descriptor.ServiceType.Name)
            .ToHashSet(StringComparer.Ordinal);

        var missing = demanded.Where(name => !registered.Contains(name)).OrderBy(n => n).ToList();

        missing.ShouldBeEmpty(
            "Commands resolved by a CLI handler but never registered in "
            + "CommandRegistrationModule. Each one throws at RUNTIME the first time a "
            + $"user runs it: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The inverse: a registration reachable from nothing is dead weight that will
    /// drift. "Reachable" means resolved by a dispatcher handler OR taken as a
    /// constructor dependency of a command that is itself reachable — e.g.
    /// <c>RefreshCommand</c> has no handler of its own but <c>SyncCommand</c>
    /// delegates its pull phase to it.
    /// </summary>
    [Fact]
    public void EveryRegisteredCommandType_IsReachableFromADispatcherHandler()
    {
        var services = BuildCommandServices();

        var commandTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.Namespace == "Twig.Commands" && !type.IsInterface && !type.IsAbstract)
            .Distinct()
            .ToList();

        var byName = commandTypes.ToDictionary(type => type.Name, StringComparer.Ordinal);

        // Seed the reachable set with handler-resolved commands, plus the two
        // classes ConsoleAppFramework itself instantiates via Program.cs app.Add<T>().
        var reachable = new HashSet<string>(ReadDispatcherDemandedCommandTypes(), StringComparer.Ordinal)
        {
            nameof(OhMyPoshCommands),
        };

        // Transitively close over constructor dependencies.
        var queue = new Queue<string>(reachable);
        while (queue.Count > 0)
        {
            if (!byName.TryGetValue(queue.Dequeue(), out var type)) continue;

            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    var name = parameter.ParameterType.Name;
                    if (byName.ContainsKey(name) && reachable.Add(name))
                        queue.Enqueue(name);
                }
            }
        }

        var orphaned = commandTypes
            .Select(type => type.Name)
            .Where(name => name.EndsWith("Command", StringComparison.Ordinal)
                || name.EndsWith("Commands", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !reachable.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        orphaned.ShouldBeEmpty(
            "Command types registered in CommandRegistrationModule but reachable from no CLI "
            + "handler, directly or transitively — dead registrations that will drift out of "
            + $"sync: {string.Join(", ", orphaned)}");
    }

    /// <summary>
    /// The trap that makes a bare "does it resolve?" test worthless.
    ///
    /// .NET DI selects the GREEDIEST SATISFIABLE constructor. When an optional
    /// dependency is unregistered, resolution still SUCCEEDS via a shorter overload
    /// and the capability silently degrades — this is exactly how #268 and #270
    /// shipped. So assert that every dependency of the WIDEST constructor is
    /// resolvable, which is the only thing that proves the intended constructor ran.
    /// </summary>
    [Fact]
    public void EveryRegisteredCommand_HasAllWidestConstructorDependenciesResolvable()
    {
        var services = BuildCommandServices();
        using var provider = services.BuildServiceProvider();

        var commandTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.Namespace == "Twig.Commands" && !type.IsInterface && !type.IsAbstract)
            .Distinct()
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

        commandTypes.Count.ShouldBeGreaterThan(30,
            "Command-type discovery found almost nothing — this guard has gone vacuous.");

        var degraded = new List<string>();

        foreach (var type in commandTypes)
        {
            var widest = type.GetConstructors()
                .OrderByDescending(ctor => ctor.GetParameters().Length)
                .FirstOrDefault();
            if (widest is null) continue;

            foreach (var parameter in widest.GetParameters())
            {
                var parameterType = Nullable.GetUnderlyingType(parameter.ParameterType)
                    ?? parameter.ParameterType;
                if (NonInjectedParameterTypes.Contains(parameterType) || parameterType.IsPrimitive)
                    continue;

                if (provider.GetService(parameterType) is null)
                    degraded.Add($"{type.Name}.{parameter.Name} ({parameterType.Name})");
            }
        }

        degraded.ShouldBeEmpty(
            "These constructor dependencies are unregistered. Because .NET DI picks the "
            + "greediest SATISFIABLE constructor, the command still resolves — via a "
            + "DEGRADED overload — and the capability fails silently at runtime "
            + $"(the #268/#270 failure mode): {string.Join(", ", degraded)}");
    }

    /// <summary>
    /// Reads the command types demanded by <c>GetRequiredService&lt;T&gt;()</c> calls
    /// in the CLI dispatcher source.
    /// </summary>
    private static HashSet<string> ReadDispatcherDemandedCommandTypes()
    {
        var repoRoot = BuildFixture.FindRepoRoot();
        var sources = new[]
        {
            Path.Combine(repoRoot, "src", "Twig", "Program.cs"),
            Path.Combine(repoRoot, "src", "Twig", "Commands", "OhMyPoshCommands.cs"),
        };

        var demanded = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in sources)
        {
            File.Exists(path).ShouldBeTrue(
                $"Expected CLI dispatcher source at {path}. If the file moved, update this "
                + "test — do not delete it; it is the only guard on DI registration.");

            foreach (Match match in Regex.Matches(
                File.ReadAllText(path),
                @"GetRequiredService<(?<type>\w+Commands?)>"))
            {
                demanded.Add(match.Groups["type"].Value);
            }
        }

        return demanded;
    }
}
