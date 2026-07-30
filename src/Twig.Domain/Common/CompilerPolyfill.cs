// Down-level shim for the C# discriminated-union support types.
//
// As of SDK 11.0.100-preview.5 the runtime ships UnionAttribute and IUnion in
// System.Private.CoreLib, with the same namespace and the same shape as below.
// This file is therefore compiled ONLY for net10.0, where the reference assemblies
// do not carry them yet; Twig.Domain.csproj excludes it from the net11.0 build.
//
// Without the exclusion, net11.0 fails with:
//   CS0433: The type 'IUnion' exists in both 'Twig.Domain' and 'System.Runtime'
// Without the file at all, net10.0 fails with:
//   CS0518 / CS0656: missing 'System.Runtime.CompilerServices.IUnion' / 'UnionAttribute..ctor'
//
// Delete this file (and the csproj exclusion) once net10.0 is dropped as a target
// framework, not before.

namespace System.Runtime.CompilerServices;

/// <summary>
/// Marks a type as a compiler-generated discriminated union.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class UnionAttribute : Attribute;

/// <summary>
/// Marker interface implemented by all discriminated union types.
/// </summary>
public interface IUnion
{
    object? Value { get; }
}
