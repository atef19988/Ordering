using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ordering.Application;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;

namespace Ordering.UnitTests.Abstractions;

public class DispatcherTests
{
    [Fact]
    public async Task Send_opens_a_transaction_runs_the_handler_and_commits()
    {
        var (dispatcher, log) = Build();

        var result = await dispatcher.Send(new Echo("hi"));

        Assert.True(result.IsSuccess);
        Assert.Equal("hi", result.Value);
        Assert.Equal(new[] { "Begin", "Handle", "Commit" }, log);
    }

    [Fact]
    public async Task Send_rolls_back_when_the_handler_returns_a_failure()
    {
        var (dispatcher, log) = Build();

        var result = await dispatcher.Send(new Echo("fail"));

        Assert.True(result.IsFailure);
        Assert.Equal("echo.refused", result.Error.Code);
        Assert.Equal(new[] { "Begin", "Handle", "Rollback" }, log);
    }

    [Fact]
    public async Task Send_rolls_back_and_rethrows_when_the_handler_throws()
    {
        var (dispatcher, log) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Send(new Echo("throw")));

        Assert.Equal(new[] { "Begin", "Handle", "Rollback" }, log);
    }

    [Fact]
    public async Task Send_short_circuits_on_validation_failure_before_any_transaction()
    {
        var (dispatcher, log) = Build();

        var result = await dispatcher.Send(new Echo(string.Empty));

        var error = Assert.IsType<ValidationError>(result.Error);
        Assert.Equal(ValidationError.ValidationErrorCode, error.Code);
        Assert.Equal(new[] { "'Text' must not be empty." }, error.Errors["Text"]);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Send_void_command_returns_a_plain_result()
    {
        var (dispatcher, log) = Build();

        var result = await dispatcher.Send(new Touch());

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Begin", "Handle", "Commit" }, log);
    }

    [Fact]
    public async Task Query_runs_the_handler_without_a_transaction()
    {
        var (dispatcher, log) = Build();

        var result = await dispatcher.Query(new Count(3));

        Assert.Equal(3, result.Value);
        Assert.Equal(new[] { "Handle" }, log);
    }

    [Fact]
    public async Task Send_throws_when_no_handler_is_registered()
    {
        var (dispatcher, _) = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.Send(new Orphan()));
    }

    [Fact]
    public async Task BaseCommandHandler_saves_changes_only_on_success()
    {
        var (dispatcher, log) = Build();

        await dispatcher.Send(new Store(1));
        Assert.Equal(new[] { "Begin", "HandleCore@2026", "SaveChanges", "Commit" }, log);

        log.Clear();

        await dispatcher.Send(new Store(-1));
        Assert.Equal(new[] { "Begin", "HandleCore@2026", "Rollback" }, log);
    }

    private static (IDispatcher Dispatcher, CallLog Log) Build()
    {
        var services = new ServiceCollection();
        var testAssembly = typeof(DispatcherTests).Assembly;

        services.AddApplication().AddRequestHandlers(testAssembly);
        services.AddValidatorsFromAssembly(testAssembly, includeInternalTypes: true);
        services.AddSingleton<CallLog>();
        services.AddSingleton<IUnitOfWork, FakeUnitOfWork>();
        services.AddSingleton<IClock, FakeClock>();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        // No ValidateOnBuild: AddApplication registers every real handler, whose repositories live in
        // Infrastructure and are deliberately absent here. Handlers resolve lazily per Send, so the
        // fakes above are still checked; the full graph is validated by the host in ApiSmokeTests.
        var scope = services
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true })
            .CreateScope();

        return (scope.ServiceProvider.GetRequiredService<IDispatcher>(), scope.ServiceProvider.GetRequiredService<CallLog>());
    }
}
