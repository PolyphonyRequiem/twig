// Polyfill for C# 15 discriminated union support types.
// Required until the .NET runtime ships these natively (expected GA in .NET 11).
// Once the runtime provides them, this file can be removed.

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
