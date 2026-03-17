# MSTest v2/v3 to v4 Migration Guide

Complete migration guide for upgrading MSTest projects from v2 or v3 to v4.

## Table of Contents

- [Overview](#overview)
- [Source Breaking Changes](#source-breaking-changes)
- [Behavior Breaking Changes](#behavior-breaking-changes)
- [Migration Patterns](#migration-patterns)
- [Project Configuration](#project-configuration)

---

## Overview

MSTest v4 introduces significant improvements but requires code changes. Key points:

- **Not binary compatible** - Libraries compiled against v3 must be recompiled
- **Minimum .NET 8** - Dropped support for .NET Core 3.1 through .NET 7
- **.NET Framework 4.6.2** still supported
- **ExpectedException removed** - Must migrate to Assert.ThrowsExactly
- **New assertion methods** - More expressive APIs available
- **Stricter analyzers** - Many rules now default to Warning severity

### Runner / platform notes (Microsoft.Testing.Platform vs VSTest)

MSTest v4 behavior can depend on which runner is executing your tests:

- **MSTest.Sdk uses Microsoft.Testing.Platform by default**. To switch to VSTest, set `UseVSTest=true` in your test project.
- Some runtime defaults and behaviors may differ by runner; if your CI behavior matters, prefer setting options explicitly (for example in `.runsettings` or `testconfig.json`).
- If you rely on VSTest tooling (for example `vstest.console`), ensure your project has the required VSTest support (see the MSTest v3→v4 migration guide section about MSTest.Sdk and VSTest).

---

## Source Breaking Changes

These changes will cause compilation errors that must be fixed.

### ExpectedExceptionAttribute Removed (CRITICAL)

```csharp
// ❌ v2/v3: Compilation error in v4
[TestMethod]
[ExpectedException(typeof(ArgumentNullException))]
public void OldPattern()
{
    ThrowingMethod();
}

// ✅ v4: Synchronous methods
[TestMethod]
public void NewPattern()
{
    Assert.ThrowsExactly<ArgumentNullException>(() => ThrowingMethod());
}

// ✅ v4: Async methods
[TestMethod]
public async Task NewPatternAsync()
{
    await Assert.ThrowsExactlyAsync<ArgumentNullException>(
        async () => await ThrowingMethodAsync());
}

// ✅ v4: Inspect exception properties
[TestMethod]
public void NewPatternWithInspection()
{
    var ex = Assert.ThrowsExactly<ArgumentException>(() => ThrowingMethod());
    Assert.AreEqual("paramName", ex.ParamName);
}
```

### Assert.ThrowsException Deprecated Overloads Removed

```csharp
// ❌ Deprecated Assert.ThrowsException APIs are removed in v4
Assert.ThrowsException<T>(action);

// ✅ Use the newer APIs
Assert.Throws<T>(action);            // Allows derived types
Assert.ThrowsExactly<T>(action);     // Exact type only
```

### Assert APIs Signature Change

Message parameter no longer accepts `object[]` format arguments:

```csharp
// ❌ Old pattern - no longer supported
Assert.AreEqual(expected, actual, "Value was {0}", actual);

// ✅ v4: Use string interpolation
Assert.AreEqual(expected, actual, $"Value was {actual}");
```

### Assert.IsInstanceOfType Out Parameter Changed

```csharp
// ❌ Old pattern
Assert.IsInstanceOfType<T>(x, out var t);

// ✅ v4: Returns the value directly
var t = Assert.IsInstanceOfType<T>(x);
```

### TestContext.Properties Type Changed

```csharp
// ❌ Old: IDictionary (non-generic)
if (TestContext.Properties.Contains("key")) { }

// ✅ v4: IDictionary<string, object>
if (TestContext.Properties.ContainsKey("key")) { }
```

### TestContext.ManagedType Removed

```csharp
// ❌ Removed
var type = TestContext.ManagedType;

// ✅ v4: Use FullyQualifiedTestClassName
var className = TestContext.FullyQualifiedTestClassName;
```

### TestTimeout Enum Removed

```csharp
// ❌ Removed
[Timeout(TestTimeout.Infinite)]

// ✅ v4: Use int.MaxValue
[Timeout(int.MaxValue)]
```

### ClassCleanupBehavior Enum Removed

```csharp
// ❌ Removed - EndOfAssembly not supported
[ClassCleanup(ClassCleanupBehavior.EndOfClass)]
public static void Cleanup() { }

// ✅ v4: Just use [ClassCleanup] - always runs at end of class
[ClassCleanup]
public static void Cleanup() { }

// Note: For end-of-assembly cleanup, use [AssemblyCleanup]
```

### TestMethodAttribute Changes

If you have custom TestMethodAttribute:

```csharp
// ❌ Old: Synchronous Execute method
public sealed class MyTestMethodAttribute : TestMethodAttribute
{
    public override TestResult[] Execute(ITestMethod testMethod)
    {
        return result;
    }
}

// ✅ v4: Async ExecuteAsync method
public sealed class MyTestMethodAttribute : TestMethodAttribute
{
    public override Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        return Task.FromResult(result);
    }
}

// ✅ v4: Constructor with caller info
public class MyTestMethodAttribute : TestMethodAttribute
{
    public MyTestMethodAttribute(
        [CallerFilePath] string callerFilePath = "", 
        [CallerLineNumber] int callerLineNumber = -1)
        : base(callerFilePath, callerLineNumber)
    {
    }
}

// ❌ Old display name syntax
[TestMethodAttribute("Custom display name")]

// ✅ v4: Use named parameter
[TestMethodAttribute(DisplayName = "Custom display name")]
```

### Target Framework Requirements

- ❌ .NET Core 3.1 through .NET 7 - Dropped
- ✅ .NET 8+ - Supported
- ✅ .NET Framework 4.6.2+ - Still supported

---

## Behavior Breaking Changes

These changes may affect test behavior at runtime without compilation errors.

### TestContext Throws on Incorrect Usage

```csharp
// ❌ THROWS InvalidOperationException in v4
[AssemblyInitialize]
public static void AssemblyInit(TestContext context)
{
    var className = context.FullyQualifiedTestClassName;  // THROWS!
    var testName = context.TestName;  // THROWS!
}

// ❌ THROWS InvalidOperationException in v4
[ClassInitialize]
public static void ClassInit(TestContext context)
{
    var testName = context.TestName;  // THROWS! TestName not available
}

// ✅ TestContext properties available in TestInitialize and TestMethod
[TestInitialize]
public void TestInit()
{
    var testName = TestContext.TestName;  // OK
}
```

### TreatDiscoveryWarningsAsErrors now defaults to true (MSTest v4)

In MSTest v4, discovery warnings now fail tests by default. If you want to keep the previous behavior, set it explicitly:

```xml
<!-- test.runsettings -->
<MSTest>
  <TreatDiscoveryWarningsAsErrors>false</TreatDiscoveryWarningsAsErrors>
</MSTest>
```

See the **Runner / platform notes** section in this document for runner-dependent behavior.

### TestCase.Id Generation Changed

May affect Azure DevOps test tracking. Be aware of potential test history discontinuity.

### DisableAppDomain now defaults to true (Microsoft.Testing.Platform)

When running with Microsoft.Testing.Platform, AppDomains are disabled by default (up to 30% performance improvement). Configure if needed:

```xml
<MSTest>
  <DisableAppDomain>false</DisableAppDomain>
</MSTest>
```

See the **Runner / platform notes** section in this document for more runner-dependent behavior.

---

## Migration Patterns

### Pattern 1: ExpectedException → ThrowsExactly

```csharp
// BEFORE
[TestMethod]
[ExpectedException(typeof(InvalidOperationException))]
public void OldTest()
{
    // Arrange
    var sut = new MyService();
    
    // Act - exception expected here
    sut.ThrowingMethod();
    
    // Some code after might never run (unclear)
}

// AFTER
[TestMethod]
public void NewTest()
{
    // Arrange
    var sut = new MyService();
    
    // Act & Assert - clear where exception is expected
    Assert.ThrowsExactly<InvalidOperationException>(() => sut.ThrowingMethod());
}
```

### Pattern 2: DataTestMethod → TestMethod

```csharp
// BEFORE
[DataRow("input1", "expected1")]
[DataRow("input2", "expected2")]
[DataTestMethod]  // Separate attribute needed
public void OldDataTest(string input, string expected) { }

// AFTER
[DataRow("input1", "expected1")]
[DataRow("input2", "expected2")]
[TestMethod]  // TestMethod now works with DataRow
public void NewDataTest(string input, string expected) { }
```

### Pattern 3: Collection Count Assertions

```csharp
// BEFORE
Assert.AreEqual(3, collection.Count);
Assert.AreEqual(0, list.Count);
Assert.IsFalse(headers.Contains(headerName));

// AFTER
Assert.HasCount(3, collection);
Assert.IsEmpty(list);
Assert.DoesNotContain(headerName, headers);
```

### Pattern 4: String Assertions

```csharp
// BEFORE
Assert.IsTrue(result.StartsWith(expected));
Assert.IsTrue(result.EndsWith(".txt"));

// AFTER
Assert.StartsWith(expected, result);
Assert.EndsWith(".txt", result);
```

### Pattern 5: Comparison Assertions

```csharp
// BEFORE
Assert.IsTrue(attemptedRetries > 1);
Assert.IsTrue(value >= min && value <= max);

// AFTER
Assert.IsGreaterThan(attemptedRetries, 1);
Assert.IsInRange(value, min, max);
```

### Pattern 6: Boolean Assertions

```csharp
// BEFORE
Assert.AreEqual(false, value);
Assert.AreEqual(true, value);

// AFTER
Assert.IsFalse(value);
Assert.IsTrue(value);
```

### Pattern 7: IsInstanceOfType Generic Form

```csharp
// BEFORE
Assert.IsInstanceOfType(ex, typeof(CloudException));

// AFTER
Assert.IsInstanceOfType<CloudException>(ex);

// Or capture the typed value
var cloudEx = Assert.IsInstanceOfType<CloudException>(ex);
Assert.AreEqual("error", cloudEx.ErrorCode);
```

### Pattern 8: Assert Argument Order

```csharp
// BEFORE (common mistake - wrong order)
Assert.AreEqual(actualValue, expectedValue);
Assert.AreEqual(result.Count, 5);

// AFTER (correct order - expected first)
Assert.AreEqual(expectedValue, actualValue);
Assert.AreEqual(5, result.Count);
```

---

## Project Configuration

### Update Package References

```xml
<!-- Option 1: Individual packages -->
<ItemGroup>
  <PackageReference Include="MSTest.TestFramework" Version="4.0.2" />
  <PackageReference Include="MSTest.TestAdapter" Version="4.0.2" />
  <PackageReference Include="MSTest.Analyzers" Version="4.0.2" />
</ItemGroup>

<!-- Option 2: MSTest metapackage (includes all) -->
<ItemGroup>
  <PackageReference Include="MSTest" Version="4.0.2" />
</ItemGroup>

<!-- Option 3: MSTest.Sdk (in Project Sdk attribute) -->
<Project Sdk="MSTest.Sdk/4.0.2">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
```

### Shared Packages.props (for multi-project solutions)

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <MSTestVersion>4.0.2</MSTestVersion>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageVersion Include="MSTest.TestFramework" Version="$(MSTestVersion)" />
    <PackageVersion Include="MSTest.TestAdapter" Version="$(MSTestVersion)" />
    <PackageVersion Include="MSTest.Analyzers" Version="$(MSTestVersion)" />
  </ItemGroup>
</Project>
```

### Add Parallelization (AssemblyInfo.cs)

```csharp
// Properties/AssemblyInfo.cs
using Microsoft.VisualStudio.TestTools.UnitTesting;

// Recommended: ClassLevel for safety
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
```

### Configure Analyzer Mode

```xml
<!-- In .csproj -->
<PropertyGroup>
  <!-- Options: None, Default, Recommended, All -->
  <MSTestAnalysisMode>Recommended</MSTestAnalysisMode>
</PropertyGroup>
```

---

## Migration Checklist

- [ ] Update MSTest packages to 4.0.2+
- [ ] Replace all `[ExpectedException]` with `Assert.ThrowsExactly`
- [ ] Update `TestContext.Properties.Contains()` to `ContainsKey()`
- [ ] Replace `TestContext.ManagedType` with `FullyQualifiedTestClassName`
- [ ] Replace `[Timeout(TestTimeout.Infinite)]` with `[Timeout(int.MaxValue)]`
- [ ] Remove `ClassCleanupBehavior` parameter from `[ClassCleanup]`
- [ ] Update custom `TestMethodAttribute` to use `ExecuteAsync`
- [ ] Fix assertion argument order (expected first)
- [ ] Update to .NET 8 if using .NET Core 3.1-7
- [ ] Add `AssemblyInfo.cs` with parallelization settings
- [ ] Run all tests to verify migration
- [ ] Fix any analyzer warnings

---

## Official Documentation

- [MSTest v3 → v4 Migration Guide](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-migration-v3-v4)
- [MSTest v1 → v3 Migration Guide](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-migration-from-v1-to-v3)
- [Writing Tests with MSTest](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests)
- [MSTest Assertions](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-assertions)
