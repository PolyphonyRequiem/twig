using System.Runtime.CompilerServices;

namespace Twig.TestKit;

/// <summary>
/// Assertion helpers for C# 15 discriminated union types.
/// <see cref="Shouldly.ShouldBeTestExtensions.ShouldBeOfType{T}"/> checks runtime type identity,
/// which returns the union wrapper type rather than the case type.
/// This helper unwraps the union via <see cref="IUnion.Value"/> before checking the case type.
/// </summary>
public static class UnionAssertions
{
    /// <summary>
    /// Asserts that <paramref name="actual"/> is a union wrapping case type <typeparamref name="T"/>
    /// and returns the unwrapped value.
    /// </summary>
    public static T ShouldBeUnionCase<T>(this object? actual)
    {
        // Unwrap the union via IUnion.Value if applicable
        var inner = actual is IUnion u ? u.Value : actual;

        if (inner is T t)
            return t;

        var actualType = inner?.GetType().Name ?? "null";
        throw new InvalidOperationException(
            $"Expected union case {typeof(T).Name} but was {actualType}");
    }
}
