# MSTest v4 Assertions Reference

Complete reference for MSTest v4 assertion methods with examples and best practices.

## Table of Contents

- [Assert Class](#assert-class)
- [StringAssert Class](#stringassert-class)
- [CollectionAssert Class](#collectionassert-class)
- [Best Practices](#best-practices)

---

## Assert Class

### Equality Assertions

```csharp
// Basic equality - ALWAYS put expected first, actual second
Assert.AreEqual(expected, actual);
Assert.AreEqual(5, result.Count);  // ✅ Correct order
Assert.AreEqual(result.Count, 5);  // ❌ Wrong order - triggers MSTEST0017

Assert.AreNotEqual(notExpected, actual);

// With tolerance for floating-point
Assert.AreEqual(3.14159, result, 0.001);  // delta tolerance
```

### Reference Assertions

```csharp
// Reference equality (same object instance)
Assert.AreSame(expected, actual);
Assert.AreNotSame(notExpected, actual);
```

### Null Assertions

```csharp
Assert.IsNull(value);
Assert.IsNotNull(value);
```

### Boolean Assertions

```csharp
// ✅ Preferred - Clear intent
Assert.IsTrue(condition);
Assert.IsFalse(condition);

// ❌ Avoid - Less readable
Assert.AreEqual(true, condition);   // Use Assert.IsTrue instead
Assert.AreEqual(false, condition);  // Use Assert.IsFalse instead
```

### Type Assertions

```csharp
// Generic form (preferred in v4) - returns the casted value
var typed = Assert.IsInstanceOfType<MyException>(ex);
Assert.AreEqual("message", typed.Message);  // Can access typed properties

// Non-generic form
Assert.IsInstanceOfType(obj, typeof(MyClass));

// Negative assertions
Assert.IsNotInstanceOfType<WrongType>(obj);
Assert.IsNotInstanceOfType(obj, typeof(WrongType));
```

### Collection Assertions

```csharp
// Count assertions
Assert.HasCount(3, collection);           // ✅ v4 way
Assert.AreEqual(3, collection.Count);     // Still works, but less expressive

// Empty checks
Assert.IsEmpty(collection);               // ✅ v4 way
Assert.IsNotEmpty(collection);
Assert.AreEqual(0, collection.Count);     // ❌ Less expressive

// Contains (for any IEnumerable)
Assert.Contains("item", collection);
Assert.DoesNotContain("item", collection);
Assert.ContainsSingle(collection);        // Exactly one element
```

### String Assertions

```csharp
// Prefix/Suffix
Assert.StartsWith("prefix", actual);      // ✅ v4 way
Assert.EndsWith("suffix", actual);
Assert.DoesNotStartWith("bad", actual);
Assert.DoesNotEndWith("bad", actual);

// ❌ Old way - less expressive
Assert.IsTrue(actual.StartsWith("prefix"));

// Regex matching
Assert.MatchesRegex(@"\d{3}-\d{4}", phoneNumber);
Assert.DoesNotMatchRegex(@"[A-Z]", lowercaseString);
```

### Comparison Assertions

```csharp
// Numeric comparisons
Assert.IsGreaterThan(actual, minimum);            // actual > minimum
Assert.IsGreaterThanOrEqualTo(actual, minimum);   // actual >= minimum
Assert.IsLessThan(actual, maximum);               // actual < maximum
Assert.IsLessThanOrEqualTo(actual, maximum);      // actual <= maximum

// Range checking
Assert.IsInRange(actual, min, max);               // min <= actual <= max

// Sign checking
Assert.IsPositive(value);   // value > 0
Assert.IsNegative(value);   // value < 0

// ❌ Old way - less readable
Assert.IsTrue(actual > minimum);
Assert.IsTrue(actual >= min && actual <= max);
```

### Exception Assertions (Critical for v4)

```csharp
// Synchronous - catches exact exception type
Assert.ThrowsExactly<ArgumentNullException>(() => 
{
    sut.Method(null);
});

// Asynchronous - for async methods
await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
{
    await sut.MethodAsync();
});

// Capture exception for further inspection
var ex = Assert.ThrowsExactly<ArgumentException>(() => sut.Method("bad"));
Assert.AreEqual("paramName", ex.ParamName);
Assert.StartsWith("Invalid value", ex.Message);

// For exceptions that may be wrapped
Assert.Throws<InvalidOperationException>(() => action()); // Catches derived types too
await Assert.ThrowsAsync<InvalidOperationException>(async () => await action());
```

#### ThrowsExactly vs Throws

| Method | Behavior |
| -------- | ---------- |
| `Assert.ThrowsExactly<T>` | Catches only exact type T |
| `Assert.Throws<T>` | Catches T and any derived types |

```csharp
// Example: CustomException : InvalidOperationException

Assert.ThrowsExactly<InvalidOperationException>(() => throw new CustomException()); // ❌ FAILS
Assert.Throws<InvalidOperationException>(() => throw new CustomException());        // ✅ PASSES
```

### Inconclusive and Fail

```csharp
// Mark test as inconclusive (skipped with warning)
Assert.Inconclusive("Feature not implemented yet");

// Force test failure
Assert.Fail("Unexpected execution path reached");

// ❌ Don't use these patterns
Assert.IsTrue(false);           // Use Assert.Fail() instead - triggers MSTEST0025
Assert.IsTrue(false, "reason"); // Use Assert.Fail("reason") instead
```

---

## StringAssert Class

Specialized assertions for string comparisons:

> ⚠️ **Parameter Order Warning:** `Assert` and `StringAssert` classes use **opposite parameter orders**!
>
> - `Assert.StartsWith(expectedPrefix, value)` - expected FIRST
> - `StringAssert.StartsWith(value, substring)` - actual value FIRST
>
> This is a common source of confusion. Double-check which class you're using!

```csharp
// Contains substring
StringAssert.Contains(haystack, "needle");

// Pattern matching
StringAssert.Matches(actual, new Regex(@"\d+"));
StringAssert.DoesNotMatch(actual, new Regex(@"[A-Z]"));

// Prefix/Suffix
StringAssert.StartsWith(actual, "prefix");
StringAssert.EndsWith(actual, "suffix");
```

---

## CollectionAssert Class

Specialized assertions for collections:

```csharp
// Element type assertions
CollectionAssert.AllItemsAreInstancesOfType(collection, typeof(MyClass));
CollectionAssert.AllItemsAreNotNull(collection);
CollectionAssert.AllItemsAreUnique(collection);

// Equality (same elements, same order)
CollectionAssert.AreEqual(expected, actual);
CollectionAssert.AreNotEqual(notExpected, actual);

// Equivalence (same elements, any order)
CollectionAssert.AreEquivalent(expected, actual);
CollectionAssert.AreNotEquivalent(notExpected, actual);

// Contains
CollectionAssert.Contains(collection, item);
CollectionAssert.DoesNotContain(collection, item);

// Subset
CollectionAssert.IsSubsetOf(subset, superset);
CollectionAssert.IsNotSubsetOf(notSubset, superset);
```

---

## Best Practices

### 1. Use Specific Assertions

```csharp
// ❌ Generic - less informative failure message
Assert.IsTrue(collection.Count == 3);

// ✅ Specific - better failure message shows actual count
Assert.HasCount(3, collection);
```

### 2. Always Provide Message for Complex Assertions

```csharp
// For non-obvious assertions, add context
Assert.IsTrue(
    result.ProcessedItems.All(x => x.Status == Status.Complete),
    "All items should be marked as complete after processing");
```

### 3. One Assert Per Test (Recommended)

```csharp
// ✅ Preferred - Single responsibility
[TestMethod]
public void Calculate_ValidInput_ReturnsCorrectValue()
{
    var result = _sut.Calculate(5);
    Assert.AreEqual(25, result);
}

[TestMethod]
public void Calculate_ValidInput_SetsProcessedFlag()
{
    _sut.Calculate(5);
    Assert.IsTrue(_sut.HasProcessed);
}

// ⚠️ Acceptable for related assertions on same result
[TestMethod]
public void GetUser_ValidId_ReturnsUserWithDetails()
{
    var user = _sut.GetUser(123);
    
    Assert.IsNotNull(user);
    Assert.AreEqual(123, user.Id);
    Assert.IsNotEmpty(user.Name);
}
```

### 4. Use Assert Messages Wisely

```csharp
// ❌ Redundant message - assertion already says this
Assert.IsTrue(result.IsValid, "result should be valid");

// ✅ Useful message - provides context not in assertion
Assert.IsTrue(result.IsValid, $"Validation failed for input: '{input}' with errors: {string.Join(", ", result.Errors)}");
```

### 5. Prefer Strongly-Typed Assertions

```csharp
// ❌ Loses type information
Assert.AreEqual(expected, actual);

// ✅ Explicit type for clarity when needed
Assert.AreEqual<decimal>(expected, actual);
```

---

## Official Documentation

- [MSTest Assertions](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-assertions)
- [Writing Tests with MSTest](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests)
