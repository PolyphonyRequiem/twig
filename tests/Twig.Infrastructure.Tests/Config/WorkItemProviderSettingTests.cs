using Shouldly;
using Twig.Infrastructure.Config;
using Xunit;

namespace Twig.Infrastructure.Tests.Config;

public sealed class WorkItemProviderSettingTests
{
    [Fact]
    public void AbsentProviderUsesAdo()
        => new TwigConfiguration().ResolveWorkItemProviderKind().ShouldBe("ado");

    [Theory]
    [InlineData("ADO")]
    [InlineData("jira")]
    [InlineData("")]
    public void UnrecognizedStoredProviderFailsClosed(string stored)
    {
        var ex = Should.Throw<TwigConfigurationException>(() => WorkItemProviderSetting.Resolve(stored));
        ex.Message.ShouldStartWith("provider-unknown:");
    }
}
