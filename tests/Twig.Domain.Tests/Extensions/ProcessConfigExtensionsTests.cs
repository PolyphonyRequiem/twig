using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Extensions;

public class ProcessConfigExtensionsTests
{
    [Fact]
    public void SafeGetConfiguration_NullProvider_ReturnsNull()
    {
        IProcessConfigurationProvider? provider = null;

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_ProviderThrows_ReturnsNull()
    {
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Throws(new InvalidOperationException("No config"));

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_ValidType_ReturnsTypeConfig()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("User Story");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldNotBeEmpty();
    }

    [Fact]
    public void SafeGetConfiguration_UnknownType_ReturnsNull()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("NonExistentType");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_EmptyType_ReturnsNull()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("");

        result.ShouldBeNull();
    }

    [Fact]
    public void SafeGetConfiguration_AgileTask_HasClosedAsCompleted()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldContain(e =>
            e.Name == "Closed" && e.Category == Enums.StateCategory.Completed);
    }

    [Fact]
    public void SafeGetConfiguration_BasicTask_HasDoneAsCompleted()
    {
        var config = ProcessConfigBuilder.Basic();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        var result = provider.SafeGetConfiguration("Task");

        result.ShouldNotBeNull();
        result.StateEntries.ShouldContain(e =>
            e.Name == "Done" && e.Category == Enums.StateCategory.Completed);
    }

    [Fact]
    public void SafeGetConfiguration_CaseInsensitiveTypeLookup()
    {
        var config = ProcessConfigBuilder.Agile();
        var provider = Substitute.For<IProcessConfigurationProvider>();
        provider.GetConfiguration().Returns(config);

        // WorkItemType.Parse normalises known types
        var result = provider.SafeGetConfiguration("user story");

        result.ShouldNotBeNull();
    }
}
