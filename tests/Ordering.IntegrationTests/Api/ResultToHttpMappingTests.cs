using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Api.Endpoints;
using Ordering.Application.Abstractions;
using Ordering.Domain.Common;

namespace Ordering.IntegrationTests.Api;

public class ResultToHttpMappingTests
{
    [Fact]
    public void Success_with_a_value_maps_to_200()
    {
        var response = Result.Success("ok").ToHttpResult();

        var ok = Assert.IsType<Ok<string>>(response);
        Assert.Equal("ok", ok.Value);
    }

    [Fact]
    public void Success_without_a_value_maps_to_204()
    {
        Assert.IsType<NoContent>(Result.Success().ToHttpResult());
    }

    [Fact]
    public void Success_can_choose_its_own_response()
    {
        var response = Result.Success(7).ToHttpResult(id => TypedResults.Created($"/api/orders/{id}", id));

        var created = Assert.IsType<Created<int>>(response);
        Assert.Equal("/api/orders/7", created.Location);
        Assert.Equal(7, created.Value);
    }

    [Theory]
    [InlineData(ErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(ErrorType.Failure, StatusCodes.Status500InternalServerError)]
    public void Failure_maps_error_type_to_status_and_exposes_the_code(ErrorType type, int expectedStatus)
    {
        var error = new Error("thing.code", "Something specific.", type);

        var response = Result.Failure(error).ToHttpResult();

        var problem = Assert.IsType<ProblemHttpResult>(response);
        Assert.Equal(expectedStatus, problem.StatusCode);
        Assert.Equal("thing.code", problem.ProblemDetails.Extensions["code"]);
        Assert.Equal("Something specific.", problem.ProblemDetails.Detail);
    }

    [Theory]
    [InlineData(ErrorType.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorType.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public async Task Retryable_failure_adds_a_retry_after_header_on_any_status(ErrorType type, int expectedStatus)
    {
        var error = new RetryableError("thing.busy", "Come back later.", type, RetryAfterSeconds: 1);

        var response = Result.Failure(error).ToHttpResult();

        var withHeader = Assert.IsType<ResultExtensions.WithRetryAfter>(response);
        var problem = Assert.IsType<ProblemHttpResult>(withHeader.Inner);
        Assert.Equal(expectedStatus, problem.StatusCode);
        Assert.Equal(1, problem.ProblemDetails.Extensions["retryAfterSeconds"]);

        var httpContext = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        await response.ExecuteAsync(httpContext);
        Assert.Equal("1", httpContext.Response.Headers.RetryAfter);
        Assert.Equal(expectedStatus, httpContext.Response.StatusCode);
    }

    [Fact]
    public void Validation_failure_maps_to_400_with_per_property_errors()
    {
        var error = new ValidationError(new Dictionary<string, string[]> { ["Quantity"] = ["Must be greater than 0."] });

        var response = Result.Failure<int>(error).ToHttpResult();

        var problem = Assert.IsType<ValidationProblem>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Equal(new[] { "Must be greater than 0." }, problem.ProblemDetails.Errors["Quantity"]);
        Assert.Equal(ValidationError.ValidationErrorCode, problem.ProblemDetails.Extensions["code"]);
    }
}
