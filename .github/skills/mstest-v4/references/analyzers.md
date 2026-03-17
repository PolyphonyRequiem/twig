# MSTest Analyzers Reference

Complete reference for MSTest analyzer codes, their meanings, and how to fix them.

## Table of Contents

- [Analyzer Configuration](#analyzer-configuration)
- [Design Rules](#design-rules)
- [Usage Rules](#usage-rules)
- [Performance Rules](#performance-rules)
- [Common Fixes](#common-fixes)

---

## Analyzer Configuration

### MSTestAnalysisMode Property

Configure analyzer behavior in your `.csproj`:

```xml
<PropertyGroup>
  <!-- Options: None, Default, Recommended, All -->
  <MSTestAnalysisMode>Recommended</MSTestAnalysisMode>
</PropertyGroup>
```

| Mode | Behavior |
| ------ | ---------- |
| `None` | All analyzers disabled |
| `Default` | Uses default severity for each rule |
| `Recommended` | Info rules escalated to warnings |
| `All` | All rules enabled as warnings |

### Per-Rule Configuration

Override specific rules in `.editorconfig`:

```ini
# .editorconfig
[*.cs]
# Disable specific rule
dotnet_diagnostic.MSTEST0015.severity = none

# Escalate to error
dotnet_diagnostic.MSTEST0035.severity = error
```

---

## Design Rules

### MSTEST0001: Explicitly enable or disable tests parallelization

**Severity:** Warning (v4 default)

This rule enforces explicit configuration of test parallelization at the assembly level.

```csharp
// ❌ Violation - no parallelization configuration
// (no assembly attribute present)

// ✅ Fix - explicitly enable parallelization
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]

// ✅ Fix - explicitly disable parallelization
[assembly: DoNotParallelize]
```

### MSTEST0003: Test methods should have valid layout

**Severity:** Error in Recommended/All modes

```csharp
// ❌ Violations
[TestMethod]
public int InvalidReturn() { return 1; }  // Must return void or Task

[TestMethod]
public void HasParameters(int x) { }  // No parameters without DataRow

// ✅ Fix
[TestMethod]
public void ValidTest() { }

[TestMethod]
public async Task ValidAsyncTest() { }

[TestMethod]
[DataRow(1)]
public void ValidDataDriven(int x) { }
```

### MSTEST0007: Use attribute on test method

**Severity:** Warning (v4 default)

This rule fires when a method has test-related attributes (like `[DataRow]`, `[Timeout]`, etc.) but is missing `[TestMethod]`.

```csharp
// ❌ Violation - DataRow without TestMethod
[TestClass]
public class MyTests
{
    [DataRow(1)]
    [DataRow(2)]
    public void MissingTestMethodAttribute(int x) { }
}

// ✅ Fix - Add TestMethod attribute
[TestClass]
public class MyTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void HasTestMethodAttribute(int x) { }
}
```

### MSTEST0030: Type containing test methods should be a test class

**Severity:** Warning (v4 default)

```csharp
// ❌ Violation - TestMethod without TestClass
public class MyTests
{
    [TestMethod]
    public void Test() { }
}

// ✅ Fix
[TestClass]
public class MyTests
{
    [TestMethod]
    public void Test() { }
}
```

### MSTEST0031: Do not use System.ComponentModel.DescriptionAttribute

**Severity:** Warning (v4 default)

This rule warns against using `System.ComponentModel.DescriptionAttribute` on test methods. Use MSTest's own attributes instead.

```csharp
// ❌ Violation - wrong DescriptionAttribute
using System.ComponentModel;

[TestMethod]
[Description("Tests the login functionality")]  // Wrong namespace!
public void LoginTest() { }

// ✅ Fix - use DisplayName or remove
[TestMethod]
[Microsoft.VisualStudio.TestTools.UnitTesting.Description("Tests the login functionality")]
public void LoginTest() { }

// ✅ Alternative - just remove the attribute
[TestMethod]
public void LoginTest() { }
```

### MSTEST0032: Review always-true assert condition

**Severity:** Warning (v4 default)

This rule warns when an assertion condition always evaluates to true, which makes the test meaningless.

```csharp
// ❌ Violation - condition is always true
[TestMethod]
public void TestAlwaysPasses()
{
    const bool alwaysTrue = true;
    Assert.IsTrue(alwaysTrue);  // Always passes, tests nothing
}

// ❌ Violation - comparing same reference
[TestMethod]
public void TestSameReference()
{
    var obj = new object();
    Assert.AreSame(obj, obj);  // Always true
}

// ✅ Fix - test actual behavior
[TestMethod]
public void TestActualBehavior()
{
    var result = _sut.CalculateSomething();
    Assert.IsTrue(result > 0);
}
```

> **Note:** For TestInitialize/TestCleanup signature issues, see MSTEST0008 and MSTEST0009.

---

## Usage Rules

### MSTEST0006: Use Assert.ThrowsException instead of ExpectedException

**Note:** ExpectedExceptionAttribute is removed in v4; this analyzer helps you migrate while still on v3.

```csharp
// ❌ Violation (and compilation error in v4)
[TestMethod]
[ExpectedException(typeof(ArgumentNullException))]
public void OldPattern() { ThrowingMethod(); }

// ✅ Fix
[TestMethod]
public void NewPattern()
{
    Assert.ThrowsExactly<ArgumentNullException>(() => ThrowingMethod());
}
```

### MSTEST0017: Assertion arguments should be passed in the correct order

**Severity:** Warning (v4 default)

This rule enforces correct argument order in assertions: expected value first, actual value second.

```csharp
// ❌ Violation - wrong order (actual, expected)
[TestMethod]
public void TestWithWrongOrder()
{
    var result = _sut.Calculate(5);
    Assert.AreEqual(result, 10);  // Wrong! actual is first
}

// ✅ Fix - correct order (expected, actual)
[TestMethod]
public void TestWithCorrectOrder()
{
    var result = _sut.Calculate(5);
    Assert.AreEqual(10, result);  // Correct! expected is first
}
```

> **Note:** For async void assertion issues, see MSTEST0040.

### MSTEST0023: Do not negate boolean assertions

**Severity:** Warning (v4 default)

```csharp
// ❌ Violations
Assert.IsTrue(!condition);
Assert.IsFalse(!condition);

// ✅ Fix
Assert.IsFalse(condition);
Assert.IsTrue(condition);
```

### MSTEST0024: Do not store TestContext in static member

**Severity:** Warning (v4 default)

```csharp
// ❌ Violation
[TestClass]
public class MyTests
{
    private static TestContext _context;  // Static!

    [ClassInitialize]
    public static void Init(TestContext context)
    {
        _context = context;  // Storing in static
    }
}

// ✅ Fix - use instance or access directly in methods
[TestClass]
public class MyTests
{
    public TestContext TestContext { get; set; }  // Auto-injected

    [TestMethod]
    public void Test()
    {
        var name = TestContext.TestName;  // Access instance
    }
}
```

### MSTEST0025: Use Assert.Fail instead of Assert.IsTrue(false)

**Severity:** Warning (v4 default)

```csharp
// ❌ Violations
Assert.IsTrue(false);
Assert.IsTrue(false, "Should not reach here");

// ✅ Fix
Assert.Fail();
Assert.Fail("Should not reach here");
```

### MSTEST0035: DeploymentItem can only be set on test class or test method

**Severity:** Warning (v4 default)

This rule ensures `[DeploymentItem]` is placed only on test classes or test methods.

```csharp
// ❌ Violation - DeploymentItem on assembly
[assembly: DeploymentItem("testdata.json")]  // Invalid location

// ❌ Violation - DeploymentItem on non-test method
[TestClass]
public class MyTests
{
    [DeploymentItem("config.json")]
    private void HelperMethod() { }  // Not a test method!
}

// ✅ Fix - on test class
[TestClass]
[DeploymentItem("testdata.json")]
public class MyTests { }

// ✅ Fix - on test method
[TestClass]
public class MyTests
{
    [TestMethod]
    [DeploymentItem("testdata.json")]
    public void TestWithDeployedFile() { }
}
```

> **Note:** For assertion argument order issues, see MSTEST0017.

### MSTEST0040: Do not assert in async void methods

**Severity:** Warning (v4 default)

This rule warns when assertion statements are used in `async void` methods, which can swallow exceptions and cause tests to pass incorrectly.

```csharp
// ❌ Violation - async void swallows assertion failures
[TestMethod]
public async void BadAsyncTest()
{
    await Task.Delay(100);
    Assert.IsTrue(false);  // May not fail the test!
}

// ❌ Violation - event handler pattern in tests
[TestMethod]
public void TestWithAsyncVoidHandler()
{
    async void Handler(object sender, EventArgs e)
    {
        await Task.Delay(100);
        Assert.IsTrue(false);  // Won't fail test!
    }
    // ...
}

// ✅ Fix - use async Task
[TestMethod]
public async Task GoodAsyncTest()
{
    await Task.Delay(100);
    Assert.IsTrue(true);  // Properly fails if assertion fails
}
```

### MSTEST0037: Use proper attributes

**Severity:** Warning (v4 default)

```csharp
// ❌ DataRow without TestMethod
[DataRow(1)]
public void MissingTestMethod(int x) { }

// ✅ Fix
[TestMethod]
[DataRow(1)]
public void WithTestMethod(int x) { }
```

### MSTEST0043: Use retry attribute on test method

**Severity:** Error in Recommended/All modes

Warns when retry attribute may be misused (flaky test indicator).

### MSTEST0045: Use cooperative cancellation for Timeout

**Severity:** Warning (v4 default)

This rule recommends using `CooperativeCancellation = true` with `[Timeout]` for better cancellation handling.

```csharp
// ❌ Not using cooperative cancellation
[TestMethod]
[Timeout(5000)]
public async Task LongRunningTest()
{
    await SomeOperation();  // Hard abort on timeout
}

// ✅ Fix - enable cooperative cancellation
[TestMethod]
[Timeout(5000, CooperativeCancellation = true)]
public async Task LongRunningTest(CancellationToken cancellationToken)
{
    await SomeOperation(cancellationToken);  // Graceful cancellation
}
```

> **Note:** For ClassInitialize/ClassCleanup signature issues, see MSTEST0010 and MSTEST0011.

---

## Performance Rules

### General Performance Guidance

- Prefer `Assert.IsEmpty()` over `Assert.AreEqual(0, collection.Count)`
- Prefer `Assert.HasCount(n, collection)` over `Assert.AreEqual(n, collection.Count)`
- Avoid redundant assertions

---

## Opt-In Rules

These rules are disabled in all modes and must be explicitly enabled:

### MSTEST0015: Test method should not be ignored

Enable to warn on `[Ignore]` attributes:

```ini
# .editorconfig
dotnet_diagnostic.MSTEST0015.severity = warning
```

```csharp
// ⚠️ Warning when enabled
[TestMethod]
[Ignore]
public void SkippedTest() { }
```

### MSTEST0019: Prefer TestInitialize methods over constructors

Enable to enforce TestInitialize pattern:

```csharp
// ⚠️ Warning when enabled
[TestClass]
public class MyTests
{
    public MyTests()  // Constructor for setup
    {
        // Setup code
    }
}

// ✅ Preferred
[TestClass]
public class MyTests
{
    [TestInitialize]
    public void Setup()
    {
        // Setup code
    }
}
```

### MSTEST0020: Prefer constructors over TestInitialize methods

Opposite of MSTEST0019 - enable if you prefer constructor pattern.

### MSTEST0021: Prefer Dispose over TestCleanup methods

### MSTEST0022: Prefer TestCleanup over Dispose methods

Choose one pattern and enable the corresponding rule.

---

## Common Fixes

### Quick Reference Table

| Code | Issue | Quick Fix |
| ------ | ------- | ----------- |
| MSTEST0001 | Missing parallelization config | Add `[assembly: Parallelize]` or `[assembly: DoNotParallelize]` |
| MSTEST0003 | Invalid test layout | Fix return type/parameters |
| MSTEST0006 | ExpectedException usage | Use `Assert.ThrowsExactly` |
| MSTEST0007 | Test attributes on non-test method | Add `[TestMethod]` or remove test attributes |
| MSTEST0017 | Wrong assertion argument order | Put expected value first |
| MSTEST0023 | Negated assertion | Use opposite assertion |
| MSTEST0024 | Static TestContext | Use instance property |
| MSTEST0025 | `IsTrue(false)` | Use `Assert.Fail()` |
| MSTEST0030 | Missing TestClass | Add `[TestClass]` |
| MSTEST0031 | Wrong DescriptionAttribute | Use MSTest attributes or remove |
| MSTEST0032 | Always-true assert condition | Fix assertion logic |
| MSTEST0035 | DeploymentItem misplaced | Move to test class or test method |
| MSTEST0037 | Missing attribute | Add required attribute |
| MSTEST0040 | Assert in async void | Change to `async Task` |
| MSTEST0045 | Missing cooperative cancellation | Set `CooperativeCancellation = true` |

### Bulk Fix with dotnet format

```powershell
# Fix all auto-fixable analyzer warnings
dotnet format analyzers --diagnostics MSTEST0023 MSTEST0025 --severity warn
```

---

## Suppressing Warnings

When suppression is necessary, document the reason:

```csharp
// Method 1: Attribute
[SuppressMessage("MSTest", "MSTEST0015", Justification = "Test temporarily disabled for investigation")]
[TestMethod]
[Ignore]
public void TemporarilyDisabled() { }

// Method 2: Pragma
#pragma warning disable MSTEST0024
private static TestContext _context;  // Needed for legacy code
#pragma warning restore MSTEST0024

// Method 3: EditorConfig (project-wide)
// .editorconfig
[*Tests.cs]
dotnet_diagnostic.MSTEST0015.severity = none
```

---

## Official Documentation

- [MSTest Analyzers Overview](https://learn.microsoft.com/en-us/dotnet/core/testing/mstest-analyzers/overview)
- [Design Rules](https://learn.microsoft.com/en-us/dotnet/core/testing/mstest-analyzers/design-rules)
- [Usage Rules](https://learn.microsoft.com/en-us/dotnet/core/testing/mstest-analyzers/usage-rules)
- [Performance Rules](https://learn.microsoft.com/en-us/dotnet/core/testing/mstest-analyzers/performance-rules)
