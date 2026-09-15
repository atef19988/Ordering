using Ordering.Application.Abstractions;

namespace Ordering.UnitTests.Abstractions;

public class ResultTests
{
    private static readonly Error Conflict = Error.Conflict("stock.insufficient", "Not enough stock.");

    [Fact]
    public void Success_carries_no_error()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(Error.None, result.Error);
    }

    [Fact]
    public void Failure_carries_the_error()
    {
        var result = Result.Failure(Conflict);

        Assert.True(result.IsFailure);
        Assert.Equal(Conflict, result.Error);
    }

    [Fact]
    public void Value_converts_implicitly_to_a_success()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Error_converts_implicitly_to_a_failure()
    {
        Result<int> typed = Conflict;
        Result untyped = Conflict;

        Assert.True(typed.IsFailure);
        Assert.Equal(Conflict, typed.Error);
        Assert.True(untyped.IsFailure);
        Assert.Equal(Conflict, untyped.Error);
    }

    [Fact]
    public void Value_of_a_failure_cannot_be_read()
    {
        var result = Result.Failure<int>(Conflict);

        var exception = Assert.Throws<InvalidOperationException>(() => result.Value);

        Assert.Contains(Conflict.Code, exception.Message);
    }

    [Fact]
    public void Generic_failure_factory_builds_the_concrete_result_type()
    {
        var typed = Fail<Result<string>>();
        var untyped = Fail<Result>();

        Assert.IsType<Result<string>>(typed);
        Assert.IsType<Result>(untyped);
        Assert.Equal(Conflict, typed.Error);
        Assert.Equal(Conflict, untyped.Error);

        static TResponse Fail<TResponse>()
            where TResponse : Result, IResultFactory<TResponse> => TResponse.Failure(Conflict);
    }
}
