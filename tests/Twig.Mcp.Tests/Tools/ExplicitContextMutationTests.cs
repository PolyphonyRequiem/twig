using System.Reflection;
using System.Runtime.CompilerServices;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Navigation;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Regression coverage for wayfinder 0021 — "the MCP is explicit-context only".
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these tests pin.</b> The active work item lives in a single shared SQLite row
/// (<c>SqliteContextStore.ActiveWorkItemKey</c>) — not per-connection, not per-session. The CLI
/// and the MCP server both read and write it. Five MCP mutations used to fall back to that row
/// when <c>id</c> was omitted, so this sequence silently mutated the wrong item:
/// </para>
/// <list type="number">
/// <item><description>a human runs <c>twig set 4102</c> in a shell;</description></item>
/// <item><description>a model mid-task calls <c>twig_note</c> with no id;</description></item>
/// <item><description>the note lands on 4102, not on the item the model believed it was on.</description></item>
/// </list>
/// <para>
/// Neither side was warned, and no test caught it, because both surfaces were behaving exactly as
/// specified. That is what made it a design defect rather than a bug — and it is why these tests
/// assert on the <b>shape of the contract</b> (id is structurally required; no mutation path can
/// reach <see cref="IContextStore"/>) rather than on any single tool's runtime behaviour. A test
/// that only checked one tool's happy path would not have failed on the unfixed code.
/// </para>
/// </remarks>
public sealed class ExplicitContextMutationTests : MutationToolsTestBase
{
    /// <summary>The five mutations that previously inferred their target from the shared pointer.</summary>
    private static readonly string[] MutationMethods =
    [
        nameof(MutationTools.State),
        nameof(MutationTools.Update),
        nameof(MutationTools.Patch),
        nameof(MutationTools.Note),
        nameof(MutationTools.Discard),
    ];

    /// <summary>
    /// Every mutation takes a required, non-nullable <c>id</c>. This fails on the unfixed code,
    /// where all five declared <c>int? id = null</c>.
    /// </summary>
    [Theory]
    [InlineData(nameof(MutationTools.State))]
    [InlineData(nameof(MutationTools.Update))]
    [InlineData(nameof(MutationTools.Patch))]
    [InlineData(nameof(MutationTools.Note))]
    [InlineData(nameof(MutationTools.Discard))]
    public void Mutation_RequiresExplicitId(string methodName)
    {
        var method = typeof(MutationTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();

        var id = method.GetParameters().SingleOrDefault(p => p.Name == "id");
        id.ShouldNotBeNull($"{methodName} must accept an explicit target id.");

        id.ParameterType.ShouldBe(
            typeof(int),
            $"{methodName}'s id must be a required int, not a nullable with an active-context fallback.");
        id.HasDefaultValue.ShouldBeFalse(
            $"{methodName}'s id must have no default — a default reintroduces the implied target.");
    }

    /// <summary>
    /// A mutation must land on the id it was handed even when the shared pointer names a different
    /// item — i.e. exactly the scenario the defect produced, asserted end to end.
    /// </summary>
    /// <remarks>
    /// This deliberately does <b>not</b> assert "the store is never read at all". The response
    /// envelope reports the active id as observational metadata (<c>EnvelopeBuilder</c>), and that
    /// read is permitted by design — reporting what the workspace points at is honest observation,
    /// not an implied write target. The rule constrains <b>target resolution</b>, not reporting.
    /// An earlier draft of this test conflated the two and failed against correct code.
    /// </remarks>
    [Fact]
    public async Task Note_WithExplicitId_TargetsThatId_NotTheSharedPointer()
    {
        var sut = CreateMutationSut();

        // The shared pointer says 4102 — as if a human had just run `twig set 4102` in a shell.
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(4102);

        var target = new Twig.TestKit.WorkItemBuilder(77, "The item the model named").AsTask().Build();
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(target);

        var pointee = new Twig.TestKit.WorkItemBuilder(4102, "The human's item").AsTask().Build();
        _workItemRepo.GetByIdAsync(4102, Arg.Any<CancellationToken>()).Returns(pointee);
        _adoService.FetchAsync(77, Arg.Any<CancellationToken>()).Returns(target);

        await sut.Note("a note the model intends for 77", id: 77);

        // The note landed on the named item...
        await _adoService.Received().AddCommentAsync(77, Arg.Any<string>(), Arg.Any<CancellationToken>());
        // ...and never on the one the shared pointer happened to name.
        await _adoService.DidNotReceive().AddCommentAsync(4102, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _pendingChangeStore.DidNotReceive().AddChangeAsync(
            4102, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The target-resolution helper mutations use must not touch <see cref="IContextStore"/> at all.
    /// This is the tight version of the rule: reporting elsewhere in the response pipeline is fine,
    /// but the code that decides WHAT gets mutated may not consult shared state.
    /// </summary>
    [Fact]
    public void ExplicitResolver_NeverReachesTheContextStore()
    {
        var resolve = typeof(WorkItemResolver).GetMethod(
            "ResolveExplicitAsync",
            BindingFlags.Public | BindingFlags.Static);
        resolve.ShouldNotBeNull();

        ReachesContextStore(resolve, new HashSet<MethodBase>(), depth: 0)
            .ShouldBeFalse("explicit target resolution must not depend on the shared active pointer.");
    }

    /// <summary>
    /// A mutation must never WRITE the shared pointer either. Writing it would change what the
    /// human's shell prompt displays as a side effect of a model's tool call.
    /// </summary>
    [Fact]
    public async Task Note_WithExplicitId_NeverWritesTheSharedActivePointer()
    {
        var sut = CreateMutationSut();

        var target = new Twig.TestKit.WorkItemBuilder(77, "Target").AsTask().Build();
        _workItemRepo.GetByIdAsync(77, Arg.Any<CancellationToken>()).Returns(target);

        await sut.Note("note", id: 77);

        await _contextStore.DidNotReceive()
            .SetActiveWorkItemIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The structural guarantee: no mutation may call the <b>active-context resolution</b> helpers,
    /// by any path. Checked against compiled IL rather than source so a future edit cannot
    /// reintroduce the fallback through a new helper and slip past the per-tool tests.
    /// </summary>
    /// <remarks>
    /// Targets <see cref="ActiveItemResolver"/> and <see cref="WorkItemResolver.ResolveWorkItemAsync"/>
    /// — the two ways a target can come from the shared pointer — rather than any touch of
    /// <see cref="IContextStore"/>. The envelope builder legitimately reads the store to report the
    /// active id, and every tool funnels through it, so a blanket IContextStore ban would flag
    /// correct code. See <see cref="Note_WithExplicitId_TargetsThatId_NotTheSharedPointer"/>.
    /// </remarks>
    [Fact]
    public void NoMutationPath_ResolvesATargetFromSharedContext()
    {
        var implicitResolver = typeof(WorkItemResolver).GetMethod(
            nameof(WorkItemResolver.ResolveWorkItemAsync),
            BindingFlags.Public | BindingFlags.Static);
        implicitResolver.ShouldNotBeNull();

        var offenders = new List<string>();

        foreach (var methodName in MutationMethods)
        {
            var method = typeof(MutationTools).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
            method.ShouldNotBeNull();

            if (Reaches(method, new HashSet<MethodBase>(), depth: 0, callee =>
                    callee.DeclaringType == typeof(ActiveItemResolver) ||
                    (MethodBase)callee == implicitResolver))
            {
                offenders.Add(methodName);
            }
        }

        offenders.ShouldBeEmpty(
            "these mutations can still resolve a target from the shared active pointer, so they can " +
            "mutate an item the caller never named: " + string.Join(", ", offenders));
    }

    /// <summary>Convenience wrapper: does this method reach <see cref="IContextStore"/> at all?</summary>
    private static bool ReachesContextStore(MethodBase method, HashSet<MethodBase> visited, int depth) =>
        Reaches(method, visited, depth, callee => callee.DeclaringType == typeof(IContextStore));

    /// <summary>
    /// Walks a method's IL for a call matching <paramref name="isTarget"/>, following calls into
    /// Twig-owned methods so an indirect route through a helper is caught too.
    /// </summary>
    private static bool Reaches(
        MethodBase method, HashSet<MethodBase> visited, int depth, Func<MethodBase, bool> isTarget)
    {
        // Bounded: the interesting hop (tool → resolver helper) is one level, but each async frame
        // costs an extra hop via its state machine, so the budget allows for both.
        if (depth > 6 || !visited.Add(method)) return false;

        // An async method's body is a stub that kicks off a compiler-generated state machine; the
        // real code lives in that type's MoveNext. Walking the stub alone finds nothing, which
        // silently hollows out this whole check — every method under test here is async.
        var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
        if (stateMachine is not null)
        {
            var moveNext = stateMachine.GetMethod(
                "MoveNext",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext is not null && Reaches(moveNext, visited, depth, isTarget)) return true;
        }

        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException) { return false; }
        if (body is null) return false;

        foreach (var callee in EnumerateCallees(method, body))
        {
            if (isTarget(callee)) return true;

            // Only follow calls into Twig's own code; the BCL cannot reach the context store.
            var assembly = callee.DeclaringType?.Assembly.GetName().Name;
            if (assembly is null || !assembly.StartsWith("Twig", StringComparison.Ordinal)) continue;

            if (Reaches(callee, visited, depth + 1, isTarget)) return true;
        }

        return false;
    }

    /// <summary>
    /// Yields every method referenced by a call/callvirt/newobj opcode in the given body.
    /// </summary>
    private static IEnumerable<MethodBase> EnumerateCallees(MethodBase method, MethodBody body)
    {
        var il = body.GetILAsByteArray();
        if (il is null) yield break;

        var module = method.Module;
        var typeArgs = method.DeclaringType?.GetGenericArguments();
        var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        for (var i = 0; i < il.Length - 4; i++)
        {
            // call = 0x28, callvirt = 0x6F, newobj = 0x73 — all take a 4-byte metadata token.
            var isCall = il[i] == 0x28 || il[i] == 0x6F || il[i] == 0x73;
            if (!isCall) continue;

            var token = BitConverter.ToInt32(il, i + 1);
            MethodBase? callee = null;
            try { callee = module.ResolveMethod(token, typeArgs, methodArgs); }
            catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
            {
                // Not a method token — this offset was operand bytes, not an opcode. Scanning a
                // byte at a time cannot distinguish the two, so a miss here is expected noise.
            }

            if (callee is not null) yield return callee;
        }
    }
}
