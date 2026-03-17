using Shouldly;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Tests for <see cref="AdoRemoteParser"/>: parses ADO remote URLs to extract org, project, repo.
/// </summary>
public class AdoRemoteParserTests
{
    [Fact]
    public void Parse_HttpsFormat_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://dev.azure.com/Contoso/BackendService/_git/twig");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("Contoso");
        result.Project.ShouldBe("BackendService");
        result.Repository.ShouldBe("twig");
    }

    [Fact]
    public void Parse_SshFormat_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("git@ssh.dev.azure.com:v3/Contoso/BackendService/twig");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("Contoso");
        result.Project.ShouldBe("BackendService");
        result.Repository.ShouldBe("twig");
    }

    [Fact]
    public void Parse_LegacyHttpsFormat_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://Contoso.visualstudio.com/BackendService/_git/twig");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("Contoso");
        result.Project.ShouldBe("BackendService");
        result.Repository.ShouldBe("twig");
    }

    [Fact]
    public void Parse_NonAdoUrl_ReturnsNull()
    {
        var result = AdoRemoteParser.Parse("https://github.com/contoso/twig.git");

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsNull(string? url)
    {
        var result = AdoRemoteParser.Parse(url);

        result.ShouldBeNull();
    }

    [Fact]
    public void Parse_HttpsWithUrlEncodedChars_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://dev.azure.com/MyOrg/My%20Project/_git/my-repo");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("MyOrg");
        result.Project.ShouldBe("My Project");
        result.Repository.ShouldBe("my-repo");
    }

    [Fact]
    public void Parse_HttpsWithTrailingWhitespace_TrimsAndParses()
    {
        var result = AdoRemoteParser.Parse("  https://dev.azure.com/Org/Proj/_git/repo  ");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("Org");
        result.Project.ShouldBe("Proj");
        result.Repository.ShouldBe("repo");
    }

    [Fact]
    public void Parse_HttpFormat_AlsoSupported()
    {
        var result = AdoRemoteParser.Parse("http://dev.azure.com/Org/Proj/_git/repo");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("Org");
        result.Project.ShouldBe("Proj");
        result.Repository.ShouldBe("repo");
    }

    [Fact]
    public void Parse_UrlWithExtraPath_ReturnsNull()
    {
        // URL with extra path segments after repo should not match
        var result = AdoRemoteParser.Parse("https://dev.azure.com/Org/Proj/_git/repo/extra");

        result.ShouldBeNull();
    }

    [Fact]
    public void Parse_LegacyHttpsFormat_CaseInsensitive()
    {
        var result = AdoRemoteParser.Parse("HTTPS://myorg.VISUALSTUDIO.COM/MyProj/_git/myrepo");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("myorg");
        result.Project.ShouldBe("MyProj");
        result.Repository.ShouldBe("myrepo");
    }

    [Fact]
    public void Parse_RepoWithDashes_ExtractsCorrectly()
    {
        var result = AdoRemoteParser.Parse("https://dev.azure.com/org/proj/_git/my-cool-repo");

        result.ShouldNotBeNull();
        result.Repository.ShouldBe("my-cool-repo");
    }

    // ── Edge cases ──────────────────────────────────────────────────

    [Fact]
    public void Parse_OrgWithHyphensAndNumbers_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://dev.azure.com/my-org-123/BackendService/_git/twig");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("my-org-123");
        result.Project.ShouldBe("BackendService");
        result.Repository.ShouldBe("twig");
    }

    [Fact]
    public void Parse_ProjectWithDots_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://dev.azure.com/contoso/My.Project.v2/_git/api-service");

        result.ShouldNotBeNull();
        result.Project.ShouldBe("My.Project.v2");
        result.Repository.ShouldBe("api-service");
    }

    [Fact]
    public void Parse_UrlWithNonStandardPort_ReturnsNull()
    {
        // The patterns require dev.azure.com/ with no port; a port breaks the match.
        var result = AdoRemoteParser.Parse("https://dev.azure.com:8080/org/proj/_git/repo");

        result.ShouldBeNull();
    }

    [Fact]
    public void Parse_SshWithHyphensInAllSegments_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("git@ssh.dev.azure.com:v3/my-org/my-project/my-repo");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("my-org");
        result.Project.ShouldBe("my-project");
        result.Repository.ShouldBe("my-repo");
    }

    [Fact]
    public void Parse_HttpsWithUrlEncodedHyphenInRepo_DecodesCorrectly()
    {
        // %2D is the URL-encoded form of '-'
        var result = AdoRemoteParser.Parse("https://dev.azure.com/contoso/MyProject/_git/my%2Drepo");

        result.ShouldNotBeNull();
        result.Repository.ShouldBe("my-repo");
    }

    [Fact]
    public void Parse_LegacyHttps_NumericPrefixedOrg_ExtractsComponents()
    {
        var result = AdoRemoteParser.Parse("https://123contoso.visualstudio.com/BackendService/_git/twig");

        result.ShouldNotBeNull();
        result.Organization.ShouldBe("123contoso");
        result.Project.ShouldBe("BackendService");
        result.Repository.ShouldBe("twig");
    }

    [Fact]
    public void Parse_UrlWithEmbeddedCredentials_ReturnsNull()
    {
        // Credentials before the host should not match the dev.azure.com pattern.
        var result = AdoRemoteParser.Parse("https://user:token@dev.azure.com/org/proj/_git/repo");

        result.ShouldBeNull();
    }
}
