using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Twig.Infrastructure.Tests.Persistence.Transport;

/// <summary>
/// Cycle-safe transitive walker over the IL of a set of root methods.
/// Reused by every conformance test that must prove a forbidden
/// construct does NOT sit anywhere on the reachable call graph.
///
/// <para>The scanner terminates at BCL / third-party boundaries by
/// name — a callee whose declaring assembly is not a Twig assembly is
/// still delivered to the visitor (so a forbidden BCL reach is
/// catchable) but the walk does not descend into it. This makes the
/// scan deterministic and fast while catching the two failure modes
/// tests actually need to defend against: (a) a forbidden LEAF (a
/// call into a domain seam, a Process API, a rejected R-row interface)
/// and (b) a helper method inside a Twig assembly that carries the
/// forbidden construct — the visitor recurses through it.</para>
///
/// <para>The walker is used by four canary-backed guarantees:
/// §9.1 no-authority reach from transport operations,
/// §12.3 Windows Terminal side-effect-free-probe,
/// §12.3 R1–R15 reject on the WT adapter,
/// §12.2 R1–R15 reject on the Herdr adapter.
/// Every consumer wires a canary type whose root method only CALLS a
/// helper carrying the forbidden token — that ensures the transitive
/// walk is genuinely exercised, not just the root.</para>
/// </summary>
internal static class TransportCallGraphWalker
{
    /// <summary>
    /// Visit every reachable callee and string-literal token starting
    /// from every declared method of <paramref name="rootTypes"/>.
    /// </summary>
    public static void Walk(
        IEnumerable<Type> rootTypes,
        Action<MethodBase, MethodBase>? onCallee = null,
        Action<MethodBase, string>? onLiteral = null,
        Action<MethodBase, Type>? onReferencedType = null,
        Func<MethodBase, bool>? shouldRecurse = null)
    {
        var visited = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();

        foreach (var root in rootTypes)
        {
            foreach (var m in AllDeclaredMethodsIncludingNested(root))
            {
                if (visited.Add(m)) queue.Enqueue(m);
            }
        }

        while (queue.Count > 0)
        {
            var method = queue.Dequeue();

            foreach (var token in EnumerateTokens(method))
            {
                if (token.IsString && onLiteral is not null && token.Literal is not null)
                {
                    onLiteral(method, token.Literal);
                    continue;
                }
                if (token.IsType && onReferencedType is not null && token.ReferencedType is not null)
                {
                    onReferencedType(method, token.ReferencedType);
                    continue;
                }
                if (token.IsMethod && token.Callee is not null)
                {
                    if (onCallee is not null) onCallee(method, token.Callee);
                    var calleeAssembly = token.Callee.DeclaringType?.Assembly;
                    var isTwigAssembly = calleeAssembly is not null
                        && (calleeAssembly.GetName().Name?.StartsWith("Twig", StringComparison.Ordinal) ?? false);
                    var recurse = shouldRecurse is null ? isTwigAssembly : shouldRecurse(token.Callee);
                    if (recurse && visited.Add(token.Callee))
                        queue.Enqueue(token.Callee);
                }
            }
        }
    }

    /// <summary>Convenience overload for a single root type.</summary>
    public static void Walk(
        Type rootType,
        Action<MethodBase, MethodBase>? onCallee = null,
        Action<MethodBase, string>? onLiteral = null,
        Action<MethodBase, Type>? onReferencedType = null,
        Func<MethodBase, bool>? shouldRecurse = null)
        => Walk(new[] { rootType }, onCallee, onLiteral, onReferencedType, shouldRecurse);

    public static IEnumerable<MethodBase> AllDeclaredMethodsIncludingNested(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly;
        foreach (var m in type.GetMethods(flags)) yield return m;
        foreach (var c in type.GetConstructors(flags)) yield return c;
        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var m in AllDeclaredMethodsIncludingNested(nested))
                yield return m;
    }

    public static string Describe(MethodBase method) =>
        $"{method.DeclaringType?.FullName ?? "<null>"}.{method.Name}";

    private readonly record struct WalkedToken(
        bool IsMethod,
        bool IsString,
        bool IsType,
        MethodBase? Callee,
        string? Literal,
        Type? ReferencedType);

    private static IEnumerable<WalkedToken> EnumerateTokens(MethodBase method)
    {
        MethodBody? body;
        try { body = method.GetMethodBody(); }
        catch { yield break; }
        if (body is null) yield break;
        var il = body.GetILAsByteArray();
        if (il is null) yield break;

        var module = method.Module;
        var declaringType = method.DeclaringType;
        var typeArgs = declaringType is { IsGenericType: true }
            ? declaringType.GetGenericArguments()
            : null;
        var methodArgs = method.IsGenericMethod
            ? method.GetGenericArguments()
            : null;

        var opcodes = OpcodeMap.Instance;
        int pos = 0;
        while (pos < il.Length)
        {
            int code = il[pos++];
            if (code == 0xFE)
            {
                if (pos >= il.Length) yield break;
                code = 0xFE00 | il[pos++];
            }
            if (!opcodes.TryGetValue(code, out var op)) yield break;

            if (op.OperandType == OperandType.InlineMethod)
            {
                if (pos + 4 > il.Length) yield break;
                int token = BitConverter.ToInt32(il, pos);
                MethodBase? callee = null;
                try { callee = module.ResolveMethod(token, typeArgs, methodArgs); }
                catch { }
                if (callee is not null)
                    yield return new WalkedToken(true, false, false, callee, null, null);
            }
            else if (op.OperandType == OperandType.InlineTok)
            {
                if (pos + 4 > il.Length) yield break;
                int token = BitConverter.ToInt32(il, pos);
                MethodBase? callee = null;
                Type? refType = null;
                try
                {
                    var member = module.ResolveMember(token, typeArgs, methodArgs);
                    callee = member as MethodBase;
                    if (callee is null && member is Type t) refType = t;
                }
                catch { }
                if (callee is not null)
                    yield return new WalkedToken(true, false, false, callee, null, null);
                else if (refType is not null)
                    yield return new WalkedToken(false, false, true, null, null, refType);
            }
            else if (op.OperandType == OperandType.InlineType)
            {
                if (pos + 4 > il.Length) yield break;
                int token = BitConverter.ToInt32(il, pos);
                Type? refType = null;
                try { refType = module.ResolveType(token, typeArgs, methodArgs); }
                catch { }
                if (refType is not null)
                    yield return new WalkedToken(false, false, true, null, null, refType);
            }
            else if (op.OperandType == OperandType.InlineString)
            {
                if (pos + 4 > il.Length) yield break;
                int token = BitConverter.ToInt32(il, pos);
                string? literal = null;
                try { literal = module.ResolveString(token); }
                catch { }
                if (literal is not null)
                    yield return new WalkedToken(false, true, false, null, literal, null);
            }
            else if (op.OperandType == OperandType.InlineField)
            {
                if (pos + 4 > il.Length) yield break;
                int token = BitConverter.ToInt32(il, pos);
                FieldInfo? field = null;
                try { field = module.ResolveField(token, typeArgs, methodArgs); }
                catch { }
                if (field?.DeclaringType is Type declared)
                    yield return new WalkedToken(false, false, true, null, null, declared);
            }

            pos += OperandLength(op.OperandType, il, pos);
        }
    }

    private static int OperandLength(OperandType type, byte[] il, int pos) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget => 1,
        OperandType.ShortInlineI => 1,
        OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineBrTarget => 4,
        OperandType.InlineField => 4,
        OperandType.InlineI => 4,
        OperandType.InlineMethod => 4,
        OperandType.InlineSig => 4,
        OperandType.InlineString => 4,
        OperandType.InlineTok => 4,
        OperandType.InlineType => 4,
        OperandType.ShortInlineR => 4,
        OperandType.InlineI8 => 8,
        OperandType.InlineR => 8,
        OperandType.InlineSwitch =>
            pos + 4 <= il.Length
                ? 4 + BitConverter.ToInt32(il, pos) * 4
                : il.Length - pos,
        _ => 0,
    };

    private static class OpcodeMap
    {
        internal static readonly Dictionary<int, OpCode> Instance = Build();

        private static Dictionary<int, OpCode> Build()
        {
            var dict = new Dictionary<int, OpCode>();
            foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)field.GetValue(null)!;
                dict[(ushort)op.Value] = op;
            }
            return dict;
        }
    }
}
