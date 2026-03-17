# MSTest v4 Parallelization Guide

Complete guide for configuring test parallelization and avoiding race conditions in MSTest v4.

## Table of Contents

- [Parallelization Basics](#parallelization-basics)
- [Configuration](#configuration)
- [Thread-Safety Requirements](#thread-safety-requirements)
- [Common Race Condition Patterns](#common-race-condition-patterns)
- [Best Practices](#best-practices)
- [Troubleshooting](#troubleshooting)

---

## Parallelization Basics

### Why Parallelization Matters

By default, MSTest runs tests sequentially. Enabling parallel execution can provide significant CI/CD performance benefits:

| Execution Mode | Description | Typical Speedup |
| ---------------- | ------------- | ----------------- |
| `MethodLevel` | Individual tests run in parallel | 4-8x on 8-core machines |
| `ClassLevel` | Classes run in parallel, methods within class run sequentially | 2-4x |
| `[DoNotParallelize]` | Completely sequential execution | 1x (baseline) |

```text
Sequential: 100 tests × 50ms average = 5,000ms (5 seconds)
Parallel (8 cores): 100 tests × 50ms ÷ 8 = 625ms (0.6 seconds)
```

### The Golden Rule

> **Every test should pass when run alone, and every test should pass when run with all other tests in any order.**

---

## Configuration

### Assembly-Level Configuration (AssemblyInfo.cs)

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

// RECOMMENDED: Safe default - classes run in parallel, methods run sequentially
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]

// Workers = 0: Use all available processors
// Workers = 4: Use exactly 4 threads
```

### Execution Scopes

| Scope | Behavior | When to Use |
| ------- | ---------- | ------------- |
| `ClassLevel` | Test classes run in parallel, tests within a class run sequentially | **RECOMMENDED DEFAULT** - Safe for most scenarios |
| `MethodLevel` | Individual test methods run in parallel | Only when tests are proven thread-safe |

### Disabling Parallelization

For specific test classes that cannot run in parallel:

```csharp
/// <summary>
/// Tests for file operations. DoNotParallelize required because
/// multiple tests access the same test files causing file locking issues.
/// </summary>
[TestClass]
[DoNotParallelize]  // Always add a comment explaining WHY
public class FileOperationTests
{
    // ...
}
```

### RunSettings Configuration

```xml
<!-- test.runsettings -->
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>-1</MaxCpuCount> <!-- All processors -->
  </RunConfiguration>
  <MSTest>
    <Parallelize>
      <Workers>0</Workers>
      <Scope>ClassLevel</Scope>
    </Parallelize>
  </MSTest>
</RunSettings>
```

---

## Thread-Safety Requirements

### Test Isolation Principle

Each test method must be completely independent - it should not depend on state from other tests, and should not leave state affecting other tests.

```text
Sequential Execution:     Test A → Test B → Test C  (always same order)
Parallel Execution:       Test A ─┬─ Test B         (unpredictable interleaving)
                                  └─ Test C
```

### Requirements for Parallel Tests

1. **No shared mutable state** between tests
2. **Instance fields** for mocks and test fixtures (not static)
3. **Fresh setup** in `[TestInitialize]` for each test
4. **No global state** dependencies (singletons, static properties)
5. **Isolated resources** (unique file paths, separate database records)

---

## Common Race Condition Patterns

### Pattern 1: Static Mocks (ANTI-PATTERN)

```csharp
// ❌ WRONG: Static mock shared across all tests
[TestClass]
public class MyTests
{
    private static Mock<IService> _serviceMock = new();  // SHARED!

    [TestMethod]
    public void Test1()
    {
        _serviceMock.Setup(x => x.GetValue()).Returns(1);  // Affects Test2!
    }

    [TestMethod]
    public void Test2()
    {
        _serviceMock.Setup(x => x.GetValue()).Returns(2);  // Conflicts with Test1!
    }
}

// ✅ CORRECT: Instance mock created fresh for each test
[TestClass]
public class MyTests
{
    private Mock<IService> _serviceMock;  // Instance field

    [TestInitialize]
    public void Setup()
    {
        _serviceMock = new Mock<IService>();  // Fresh mock per test
    }

    [TestMethod]
    public void Test1()
    {
        _serviceMock.Setup(x => x.GetValue()).Returns(1);  // Isolated
    }
}
```

### Pattern 2: Static Counters (ANTI-PATTERN)

```csharp
// ❌ WRONG: Static counter corrupted by parallel tests
internal static volatile int CallCount = 0;

[TestMethod]
public void TestCallCount()
{
    CallCount = 0;  // Reset
    _sut.DoWork();
    Assert.AreEqual(1, CallCount);  // May fail if other test incremented!
}

// ✅ CORRECT: Instance counter scoped to test
[TestClass]
public class MyTests
{
    private int _callCount;

    [TestInitialize]
    public void Setup() => _callCount = 0;

    [TestMethod]
    public void TestCallCount()
    {
        var stub = new TestableService(onCall: () => _callCount++);
        stub.DoWork();
        Assert.AreEqual(1, _callCount);  // Isolated
    }
}
```

### Pattern 3: Shared File Access (ANTI-PATTERN)

```csharp
// ❌ WRONG: All tests use same file
private static readonly string TestFile = "TestData/shared.txt";

[TestMethod]
public void Test1() => File.WriteAllText(TestFile, "data1");  // Locks file!

[TestMethod]
public void Test2() => File.ReadAllText(TestFile);  // IOException!

// ✅ CORRECT: Each test gets unique file
[TestMethod]
public void Test1()
{
    var testFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
    try
    {
        File.WriteAllText(testFile, "data1");
        // ... test logic ...
    }
    finally
    {
        if (File.Exists(testFile)) File.Delete(testFile);
    }
}
```

### Pattern 4: Global Singleton State (ANTI-PATTERN)

```csharp
// ❌ WRONG: Test depends on global singleton
[TestMethod]
public void TestWithGlobalState()
{
    GlobalConfig.Instance.Setting = "test-value";  // Affects ALL tests!
    var result = _sut.DoSomething();
    Assert.AreEqual(expected, result);
}

// ✅ CORRECT: Inject configuration as dependency
[TestMethod]
public void TestWithInjectedConfig()
{
    var configMock = new Mock<IConfig>();
    configMock.Setup(c => c.Setting).Returns("test-value");

    var sut = new MyClass(configMock.Object);  // Injected, not global
    var result = sut.DoSomething();
    Assert.AreEqual(expected, result);
}
```

### Pattern 5: Console/Output Redirection (ANTI-PATTERN)

```csharp
// ❌ WRONG: Console.Out is global
[TestMethod]
public void TestConsoleOutput()
{
    var writer = new StringWriter();
    Console.SetOut(writer);  // Affects ALL tests!
    _sut.WriteMessage();
    Assert.Contains("expected", writer.ToString());
}

// ✅ CORRECT: Inject the writer
[TestMethod]
public void TestConsoleOutput()
{
    var writer = new StringWriter();
    _sut.WriteMessage(writer);  // Injected, not global
    Assert.Contains("expected", writer.ToString());
}
```

---

## Best Practices

### 1. Default to ClassLevel Parallelization

```csharp
// AssemblyInfo.cs
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
```

This is the safest default - tests within a class run sequentially, providing implicit isolation for any class-level state.

### 2. Use Instance Fields, Not Static

```csharp
[TestClass]
public class MyTests
{
    // ✅ Instance fields - fresh per test method
    private Mock<IService> _serviceMock;
    private MyClass _sut;

    [TestInitialize]
    public void Setup()
    {
        _serviceMock = new Mock<IService>();
        _sut = new MyClass(_serviceMock.Object);
    }
}
```

### 3. Document Any [DoNotParallelize]

```csharp
/// <summary>
/// Tests for RevocationContext behavior. DoNotParallelize required because
/// RevocationContext.Reset() clears global state that other tests depend on.
/// TODO: Refactor to use injectable revocation service (Tech Debt: ADO#12345)
/// </summary>
[TestClass]
[DoNotParallelize]
public class RevocationContextTests
{
}
```

### 4. Use [ClassInitialize] Sparingly

Only use for truly immutable, read-only setup:

```csharp
[TestClass]
public class MyTests
{
    // ✅ OK: Static readonly test data
    private static readonly string[] ValidInputs = { "a", "b", "c" };

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        // ✅ OK: Read-only setup
        _testData = LoadTestDataFromFile();  // Immutable after load
    }

    // ❌ WRONG: Mutable setup in ClassInitialize
    // private static Mock<IService> _mock;  // Will cause race conditions!
}
```

### 5. Unique Resources Per Test

```csharp
[TestMethod]
public void TestDatabaseOperation()
{
    // Unique ID ensures no collision with parallel tests
    var uniqueId = $"test_{Guid.NewGuid():N}";
    var record = new TestRecord { Id = uniqueId, Value = "test" };
    
    _repository.Insert(record);
    var result = _repository.GetById(uniqueId);
    
    Assert.AreEqual("test", result.Value);
    
    // Cleanup
    _repository.Delete(uniqueId);
}
```

---

## Troubleshooting

### Identifying Race Conditions

**Symptoms:**

- Tests pass when run individually, fail when run together
- Tests pass sometimes, fail other times (flaky)
- Tests fail with unexpected values or state

**Diagnostic Commands:**

```powershell
# Run tests multiple times to catch intermittent failures
for ($i = 1; $i -le 10; $i++) { dotnet test --no-build -v minimal }

# Run with single thread to verify tests pass sequentially
dotnet test --no-build -- MSTest.Parallelize.Workers=1
```

### Finding Problematic Patterns

```powershell
# Find static Mock declarations
git grep "static\s\+Mock<" -- "*.cs"

# Find static volatile fields
git grep "static volatile" -- "*.cs"

# Find [DoNotParallelize] usages
git grep "\[DoNotParallelize\]" -- "*.cs"

# Find ClassInitialize with mock setup
git grep -A5 "\[ClassInitialize\]" -- "*.cs" | grep "Mock<"
```

### Decision Matrix

```text
Is the shared state mutable?
├── No (readonly/const) → ✅ Safe for MethodLevel
└── Yes
    ├── Can it be made instance-scoped?
    │   └── Yes → ✅ Refactor to instance field
    │   └── No
    │       ├── Is it external resource (file/DB/service)?
    │       │   └── Yes → Use [DoNotParallelize] with comment
    │       │   └── No → Can dependency be injected?
    │       │       └── Yes → ✅ Inject mock/fake
    │       │       └── No → Use ClassLevel + document tech debt
```

### Code Review Checklist

When reviewing test code, verify:

- [ ] No `static Mock<T>` declarations
- [ ] No `static volatile` counters
- [ ] No direct access to singletons (use injection)
- [ ] `[ClassInitialize]` only for truly immutable setup
- [ ] Any `[DoNotParallelize]` has a justification comment
- [ ] File operations use unique paths per test

---

## Handling Flaky Tests with [Retry]

When parallelization reveals timing-sensitive tests that occasionally fail, consider using the `[Retry]` attribute (new in MSTest 3.8) as a temporary measure while investigating root causes.

### Basic Usage

```csharp
[TestMethod]
[Retry(3)]  // Retry up to 3 times on failure
public void TestWithOccasionalTimingIssue()
{
    // Test that may fail due to timing sensitivity
}
```

### Retry with Delay

```csharp
[TestMethod]
[Retry(maxRetryCount: 3, delayMilliseconds: 100, backoffType: RetryBackoffType.Exponential)]
public void TestWithExponentialBackoff()
{
    // First retry after 100ms, second after 200ms, third after 400ms
}
```

### When to Use [Retry]

| Scenario | Recommendation |
| ---------- | ---------------- |
| Investigating flaky test | ✅ Use temporarily while debugging |
| External service dependency | ✅ Acceptable with documentation |
| Race condition in test code | ❌ Fix the root cause instead |
| Race condition in production code | ❌ Fix the production code |

> ⚠️ **Warning:** `[Retry]` should not be a permanent solution for race conditions. It masks problems rather than solving them. Always investigate and fix the underlying cause.

### Custom Retry Logic

You can extend `RetryBaseAttribute` for custom retry behavior:

```csharp
public class RetryOnSpecificExceptionAttribute : RetryBaseAttribute
{
    private readonly Type _exceptionType;
    
    public RetryOnSpecificExceptionAttribute(Type exceptionType, int maxRetries = 3) 
        : base(maxRetries)
    {
        _exceptionType = exceptionType;
    }
    
    protected override bool ShouldRetry(TestResult result)
    {
        return result.Outcome == UnitTestOutcome.Failed 
            && result.TestFailureException?.GetType() == _exceptionType;
    }
}

// Usage
[TestMethod]
[RetryOnSpecificException(typeof(TimeoutException), maxRetries: 2)]
public void TestExternalService() { }
```

---

## MSTest v4 Behavior Changes

### DisableAppDomain now defaults to true (Microsoft.Testing.Platform)

See [Migration Guide](migration.md) for runner/platform notes and configuration examples.

### TreatDiscoveryWarningsAsErrors now defaults to true (MSTest v4)

In MSTest v4, discovery warnings now fail tests by default. Fix issues or set it explicitly:

```xml
<MSTest>
  <TreatDiscoveryWarningsAsErrors>false</TreatDiscoveryWarningsAsErrors>
</MSTest>
```

See [Migration Guide](migration.md) for runner/platform notes.

---

## Official Documentation

- [MSTest Parallelization](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests#parallel-execution)
- [Writing Tests with MSTest](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests)
- [MSTest v3 → v4 Migration Guide](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-migration-v3-v4)
