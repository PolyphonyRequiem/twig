using Shouldly;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public class AdoUnexpectedResponseExceptionTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var ex = new AdoUnexpectedResponseException(200, "text/html", "https://dev.azure.com/org/proj/_apis/wit/workitems/42", "<html>login</html>");

        ex.StatusCode.ShouldBe(200);
        ex.ContentType.ShouldBe("text/html");
        ex.RequestUrl.ShouldBe("https://dev.azure.com/org/proj/_apis/wit/workitems/42");
        ex.BodySnippet.ShouldBe("<html>login</html>");
    }

    [Fact]
    public void Constructor_SetsDescriptiveMessage()
    {
        var ex = new AdoUnexpectedResponseException(200, "text/html", "https://example.com/api", "snippet");

        ex.Message.ShouldBe("ADO returned non-JSON response (HTTP 200, Content-Type: text/html). URL: https://example.com/api. Body: snippet");
    }

    [Fact]
    public void InheritsFromAdoException()
    {
        var ex = new AdoUnexpectedResponseException(302, "text/plain", "https://example.com", "redirect");

        ex.ShouldBeAssignableTo<AdoException>();
    }

    [Fact]
    public void EmptyStrings_AreAccepted()
    {
        var ex = new AdoUnexpectedResponseException(0, "", "", "");

        ex.StatusCode.ShouldBe(0);
        ex.ContentType.ShouldBe(string.Empty);
        ex.RequestUrl.ShouldBe(string.Empty);
        ex.BodySnippet.ShouldBe(string.Empty);
        ex.Message.ShouldContain("HTTP 0");
    }

}
