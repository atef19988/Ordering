using FluentValidation;
using Ordering.Application.Abstractions;
using Ordering.Application.Abstractions.Messaging;
using Ordering.Domain.Common;

namespace Ordering.UnitTests.Abstractions;

/// <summary>Ordered record of everything the pipeline touched, shared by the fakes and the handlers.</summary>
internal sealed class CallLog : List<string>;

internal sealed class FakeUnitOfWork(CallLog log) : IUnitOfWork
{
    public Task BeginTransactionAsync(CancellationToken cancellationToken) => Record("Begin");

    public Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken) => Record($"LockTimeout={timeout.TotalSeconds}s");

    public Task CommitAsync(CancellationToken cancellationToken) => Record("Commit");

    public Task RollbackAsync(CancellationToken cancellationToken) => Record("Rollback");

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        log.Add("SaveChanges");
        return Task.FromResult(1);
    }

    private Task Record(string call)
    {
        log.Add(call);
        return Task.CompletedTask;
    }
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

internal sealed record Echo(string Text) : ICommand<string>;

internal sealed class EchoValidator : AbstractValidator<Echo>
{
    public EchoValidator() => RuleFor(e => e.Text).NotEmpty();
}

internal sealed class EchoHandler(CallLog log) : ICommandHandler<Echo, string>
{
    public Task<Result<string>> Handle(Echo command, CancellationToken cancellationToken)
    {
        log.Add("Handle");

        return Task.FromResult<Result<string>>(command.Text switch
        {
            "throw" => throw new InvalidOperationException("boom"),
            "fail" => Error.Conflict("echo.refused", "Refused."),
            _ => command.Text,
        });
    }
}

internal sealed record Touch : ICommand;

internal sealed class TouchHandler(CallLog log) : ICommandHandler<Touch>
{
    public Task<Result> Handle(Touch command, CancellationToken cancellationToken)
    {
        log.Add("Handle");
        return Task.FromResult(Result.Success());
    }
}

internal sealed record Count(int N) : IQuery<int>;

internal sealed class CountHandler(CallLog log) : IQueryHandler<Count, int>
{
    public Task<Result<int>> Handle(Count query, CancellationToken cancellationToken)
    {
        log.Add("Handle");
        return Task.FromResult<Result<int>>(query.N);
    }
}

/// <summary>Has no handler on purpose.</summary>
internal sealed record Orphan : ICommand;

internal sealed record Store(int Value) : ICommand<int>;

internal sealed class StoreHandler(IUnitOfWork unitOfWork, IClock clock, CallLog log) : BaseCommandHandler<Store, int>(unitOfWork, clock)
{
    protected override Task<Result<int>> HandleCore(Store command, CancellationToken cancellationToken)
    {
        log.Add($"HandleCore@{Clock.UtcNow:yyyy}");

        return Task.FromResult<Result<int>>(command.Value < 0
            ? Error.Conflict("store.negative", "Negative values are refused.")
            : command.Value);
    }
}
