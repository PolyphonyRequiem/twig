---
name: mstest-v4
description: Guide for writing .NET unit tests following MSTest v4 best practices. Use when developers need to (1) Write new unit tests with MSTest v4 patterns, (2) Migrate tests from MSTest v2/v3 to v4, (3) Fix MSTest analyzer warnings (MSTEST0001-MSTEST0045), (4) Configure test parallelization safely, (5) Use proper assertion methods (Assert.ThrowsExactly, Assert.HasCount, Assert.StartsWith, etc.), (6) Handle exception testing (replace ExpectedException attribute), (7) Set up test classes with proper attributes and lifecycle methods, or (8) Troubleshoot race conditions in parallel test execution.
---

# MSTest v4 Unit Testing Guide

This skill provides guidance for writing high-quality .NET unit tests following MSTest v4 best practices.

## Quick Reference

### Test Class Structure

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class MyServiceTests
{
    private Mock<IDependency> _dependencyMock;
    private MyService _sut;  // System Under Test

    [TestInitialize]
    public void Setup()
    {
        _dependencyMock = new Mock<IDependency>();
        _sut = new MyService(_dependencyMock.Object);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Dispose resources if needed
    }

    [TestMethod]
    public void MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var input = "test";
        
        // Act
        var result = _sut.Process(input);
        
        // Assert
        Assert.AreEqual("expected", result);
    }
}
```

### Exception Testing (v4 Required Pattern)

```csharp
// ❌ REMOVED in v4 - Do NOT use ExpectedExceptionAttribute
[ExpectedException(typeof(ArgumentNullException))]  // Compilation error in v4
public void OldPattern() { }

// ✅ REQUIRED in v4 - Use Assert.ThrowsExactly
[TestMethod]
public void Method_NullInput_ThrowsArgumentNullException()
{
    Assert.ThrowsExactly<ArgumentNullException>(() => _sut.Method(null));
}

// ✅ For async methods
[TestMethod]
public async Task MethodAsync_NullInput_ThrowsArgumentNullException()
{
    await Assert.ThrowsExactlyAsync<ArgumentNullException>(
        async () => await _sut.MethodAsync(null));
}

// ✅ Capture and inspect the exception
[TestMethod]
public void Method_InvalidInput_ThrowsWithMessage()
{
    var ex = Assert.ThrowsExactly<ArgumentException>(() => _sut.Method("bad"));
    Assert.AreEqual("Input is invalid", ex.Message);
    Assert.AreEqual("input", ex.ParamName);
}
```

### Assertion Argument Order

MSTest analyzers can enforce `expected, actual` order:

```csharp
// ❌ WRONG - Causes MSTEST0017 warning
Assert.AreEqual(result.Count, 5);
Assert.AreEqual(actualValue, expectedValue);

// ✅ CORRECT - Expected first, actual second
Assert.AreEqual(5, result.Count);
Assert.AreEqual(expectedValue, actualValue);
```

### Modern Assertion Methods (v4)

For complete assertion API reference, see [references/assertions.md](references/assertions.md).

```csharp
// Collection assertions
Assert.HasCount(3, collection);           // Instead of Assert.AreEqual(3, collection.Count)
Assert.IsEmpty(list);                     // Instead of Assert.AreEqual(0, list.Count)
Assert.Contains("item", collection);
Assert.DoesNotContain("item", collection);

// String assertions
Assert.StartsWith("prefix", result);      // Instead of Assert.IsTrue(result.StartsWith("prefix"))
Assert.EndsWith("suffix", result);
Assert.MatchesRegex(@"\d+", result);

// Comparison assertions
Assert.IsGreaterThan(actual, 5);          // Instead of Assert.IsTrue(actual > 5)
Assert.IsLessThan(actual, 10);
Assert.IsInRange(actual, 1, 100);

// Type assertions
Assert.IsInstanceOfType<MyException>(ex); // Generic form preferred
var typed = Assert.IsInstanceOfType<CustomType>(obj); // Returns casted value

// Boolean assertions
Assert.IsTrue(condition);                 // Instead of Assert.AreEqual(true, condition)
Assert.IsFalse(condition);                // Instead of Assert.AreEqual(false, condition)
```

### Data-Driven Tests

```csharp
// [DataTestMethod] is no longer required - [TestMethod] works with [DataRow]
[TestMethod]
[DataRow("input1", "expected1")]
[DataRow("input2", "expected2")]
[DataRow(null, "default")]
public void Method_WithVariousInputs_ReturnsExpected(string input, string expected)
{
    var result = _sut.Method(input);
    Assert.AreEqual(expected, result);
}

// Dynamic data source
[TestMethod]
[DynamicData(nameof(TestCases), DynamicDataSourceType.Property)]
public void Method_DynamicData_Works(int input, int expected)
{
    Assert.AreEqual(expected, _sut.Calculate(input));
}

public static IEnumerable<object[]> TestCases => new[]
{
    new object[] { 1, 2 },
    new object[] { 2, 4 },
};
```

### Parallelization Configuration

For complete parallelization guide, see [references/parallelization.md](references/parallelization.md).

```csharp
// AssemblyInfo.cs - Configure at assembly level
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]

// Workers = 0 means use all available processors
// ExecutionScope.ClassLevel = classes run in parallel, methods within class run sequentially (RECOMMENDED SAFE DEFAULT)
// ExecutionScope.MethodLevel = individual methods run in parallel (requires thread-safe tests)
```

## Key Migration Patterns (v2/v3 → v4)

For complete migration guide, see [references/migration.md](references/migration.md).

| Before (v2/v3) | After (v4) |
| ---------------- | ------------ |
| `[ExpectedException(typeof(T))]` | `Assert.ThrowsExactly<T>(() => ...)` |
| `[DataTestMethod]` | `[TestMethod]` (works with `[DataRow]`) |
| `Assert.AreEqual(collection.Count, 5)` | `Assert.HasCount(5, collection)` |
| `Assert.IsTrue(str.StartsWith("x"))` | `Assert.StartsWith("x", str)` |
| `Assert.AreEqual(true, flag)` | `Assert.IsTrue(flag)` |
| `Assert.IsInstanceOfType(obj, typeof(T))` | `Assert.IsInstanceOfType<T>(obj)` |
| `TestContext.Properties.Contains(key)` | `TestContext.Properties.ContainsKey(key)` |
| `[ClassCleanup(ClassCleanupBehavior.EndOfClass)]` | `[ClassCleanup]` |
| `[Timeout(TestTimeout.Infinite)]` | `[Timeout(int.MaxValue)]` |

## MSTest Analyzers (Common Warnings)

For complete analyzer reference, see [references/analyzers.md](references/analyzers.md).

| Code | Issue | Fix |
| ------ | ------- | ----- |
| MSTEST0001 | Missing parallelization config | Add `[assembly: Parallelize]` or `[assembly: DoNotParallelize]` |
| MSTEST0007 | Test attributes on non-test method | Add `[TestMethod]` or remove test attributes |
| MSTEST0017 | Wrong assertion argument order | Put expected value first in `Assert.AreEqual` |
| MSTEST0023 | Negated assertion | Use `Assert.IsFalse` instead of `Assert.IsTrue(!x)` |
| MSTEST0025 | `Assert.IsTrue(false)` | Use `Assert.Fail()` |
| MSTEST0035 | DeploymentItem misplaced | Move `[DeploymentItem]` to test class or test method |
| MSTEST0040 | Assert in async void | Change to `async Task` return type |

## Critical Rules

1. **Never use `[ExpectedException]`** - Removed in v4, use `Assert.ThrowsExactly<T>()`
2. **Always put expected value first** in `Assert.AreEqual(expected, actual)`
3. **Use instance fields for mocks** - Never static mocks (causes race conditions)
4. **Prefer `[TestInitialize]`** over constructors for setup
5. **Use `ClassLevel` parallelization** unless tests are proven thread-safe

## Additional Resources

- [Assertions Reference](references/assertions.md) - Complete API for all Assert methods
- [Parallelization Guide](references/parallelization.md) - Thread-safety and race condition prevention
- [Migration Guide](references/migration.md) - Complete v2/v3 to v4 migration patterns
- [Analyzers Reference](references/analyzers.md) - All MSTest analyzer codes and fixes
