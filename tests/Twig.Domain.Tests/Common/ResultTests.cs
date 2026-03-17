using Shouldly;
using Twig.Domain.Common;
using Xunit;

namespace Twig.Domain.Tests.Common;

public class ResultTests
{
    [Fact]
    public void Ok_ReturnsSuccess()
    {
        var result = Result.Ok();
        result.IsSuccess.ShouldBeTrue();
        result.Error.ShouldBe(string.Empty);
    }

    [Fact]
    public void Fail_ReturnsFailure()
    {
        var result = Result.Fail("something went wrong");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("something went wrong");
    }

    [Fact]
    public void Ok_Generic_ReturnsSuccessWithValue()
    {
        var result = Result.Ok(42);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBe(string.Empty);
    }

    [Fact]
    public void Fail_Generic_ReturnsFailureWithError()
    {
        var result = Result.Fail<int>("bad input");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("bad input");
    }

    [Fact]
    public void Ok_Generic_String_ReturnsValue()
    {
        var result = Result<string>.Ok("hello");
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("hello");
    }

    [Fact]
    public void Fail_Generic_String_ReturnsError()
    {
        var result = Result<string>.Fail("oops");
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("oops");
    }

    [Fact]
    public void Result_Equality_SameOk()
    {
        var r1 = Result.Ok();
        var r2 = Result.Ok();
        r1.ShouldBe(r2);
    }

    [Fact]
    public void Result_Equality_SameFail()
    {
        var r1 = Result.Fail("err");
        var r2 = Result.Fail("err");
        r1.ShouldBe(r2);
    }

    [Fact]
    public void Result_Inequality_OkVsFail()
    {
        var ok = Result.Ok();
        var fail = Result.Fail("err");
        ok.ShouldNotBe(fail);
    }

    [Fact]
    public void Fail_Generic_Value_ThrowsInvalidOperationException()
    {
        var result = Result.Fail<int>("something went wrong");
        Should.Throw<InvalidOperationException>(() => { var _ = result.Value; })
            .Message.ShouldContain("something went wrong");
    }
}
