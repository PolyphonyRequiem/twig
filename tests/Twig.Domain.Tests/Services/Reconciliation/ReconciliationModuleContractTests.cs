using System.Reflection;
using Shouldly;
using Twig.Domain.Services.Reconciliation;
using Twig.Domain.Services.Sync;
using Xunit;

namespace Twig.Domain.Tests.Services.Reconciliation;

/// <summary>
/// Wayfinder 0004's deletion test, made executable.
/// </summary>
/// <remarks>
/// <para>
/// 0004 required that deleting the reconciliation module make complexity <b>reappear</b> across
/// the five sites rather than vanish: <i>"If it forwards to SyncCoordinator/RefreshOrchestrator
/// unchanged it has not earned its keep."</i> A facade would pass every behavioural test in this
/// suite while satisfying none of the ticket, so the structural claims are asserted directly.
/// </para>
/// <para>
/// These are deliberately about SHAPE, not behaviour. The behavioural guarantees live in
/// <see cref="ThreeWayMergeTests"/>; what this file prevents is the module quietly decaying back
/// into a pass-through in a later slice.
/// </para>
/// </remarks>
public class ReconciliationModuleContractTests
{
    private static readonly Assembly DomainAssembly = typeof(ThreeWayMerge).Assembly;

    /// <summary>
    /// The module must not forward to the orchestrators it was created to take work away from.
    /// If <c>Reconciliation</c> referenced <c>SyncCoordinator</c> or <c>RefreshOrchestrator</c>,
    /// deleting it would move complexity rather than reveal it.
    /// </summary>
    [Theory]
    [InlineData("SyncCoordinator")]
    [InlineData("RefreshOrchestrator")]
    [InlineData("ProtectedCacheWriter")]
    public void Module_DoesNotForwardToTheOrchestratorsItReplaces(string forbiddenTypeName)
    {
        var moduleTypes = DomainAssembly.GetTypes()
            .Where(t => t.Namespace == typeof(ThreeWayMerge).Namespace)
            .ToList();

        moduleTypes.ShouldNotBeEmpty("the Reconciliation namespace must contain the module");

        foreach (var type in moduleTypes)
        {
            var referenced = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                             | BindingFlags.Static | BindingFlags.Instance
                                             | BindingFlags.DeclaredOnly)
                .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType))
                .Concat(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance
                                       | BindingFlags.DeclaredOnly)
                    .Select(f => f.FieldType))
                .Select(t => t.Name);

            referenced.ShouldNotContain(
                forbiddenTypeName,
                $"{type.Name} must not depend on {forbiddenTypeName} — a module that forwards to " +
                "the orchestrators it replaces has not earned its keep (0004 deletion test)");
        }
    }

    /// <summary>
    /// The merge base must be a projection over rows the pending store already holds, never new
    /// persisted state. Wayfinder 0006 ruled explicitly against a persisted baseline revision:
    /// "No — do not persist a baseline revision. The baseline already exists, it is already
    /// durable ... it is pending_changes.old_value."
    /// </summary>
    [Fact]
    public void MergeBase_IntroducesNoPersistedState()
    {
        var repositoryLikeDependencies = typeof(MergeBase)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic
                       | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(f => f.FieldType.Name)
            .Where(n => n.Contains("Repository", StringComparison.Ordinal)
                        || n.Contains("Store", StringComparison.Ordinal)
                        || n.Contains("DbConnection", StringComparison.Ordinal))
            .ToList();

        repositoryLikeDependencies.ShouldBeEmpty(
            "MergeBase must project rows the caller already read, not open its own storage — " +
            "0006 ruled against persisting a baseline");
    }

    /// <summary>
    /// The module reuses <see cref="MergeResult"/> rather than minting a parallel outcome type,
    /// so the CLI and MCP surfaces keep pattern-matching one vocabulary (0002's seam shape).
    /// </summary>
    [Fact]
    public void Resolve_ReturnsTheSharedMergeResultUnion()
    {
        var resolve = typeof(ThreeWayMerge).GetMethod(nameof(ThreeWayMerge.Resolve));

        resolve.ShouldNotBeNull();
        resolve.ReturnType.ShouldBe(
            typeof(MergeResult),
            "surfaces must not have to learn a second outcome vocabulary");
    }

    /// <summary>
    /// The merge base is a required argument. An optional one would let a caller silently fall
    /// back to mirror-vs-remote — the very defect 0004 slice 3 exists to remove — while still
    /// compiling and still looking like it reconciled.
    /// </summary>
    [Fact]
    public void Resolve_RequiresTheMergeBase()
    {
        var resolve = typeof(ThreeWayMerge).GetMethod(nameof(ThreeWayMerge.Resolve));
        resolve.ShouldNotBeNull();

        var mergeBaseParam = resolve!.GetParameters()
            .SingleOrDefault(p => p.ParameterType == typeof(MergeBase));

        mergeBaseParam.ShouldNotBeNull("Resolve must take a MergeBase");
        mergeBaseParam!.HasDefaultValue.ShouldBeFalse(
            "a defaulted merge base would let callers silently degrade to a two-way compare");
        mergeBaseParam.IsOptional.ShouldBeFalse();
    }
}
