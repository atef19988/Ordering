namespace Ordering.Application.Abstractions.Messaging;

/// <summary>Anything the <see cref="IDispatcher"/> can route. Not implemented directly; use the three below.</summary>
public interface IRequest<TResponse>
    where TResponse : Result, IResultFactory<TResponse>;

/// <summary>Marker shared by both command shapes so plumbing can tell writes from reads.</summary>
public interface IBaseCommand;

public interface ICommand : IRequest<Result>, IBaseCommand;

public interface ICommand<TResult> : IRequest<Result<TResult>>, IBaseCommand;

public interface IQuery<TResult> : IRequest<Result<TResult>>;
