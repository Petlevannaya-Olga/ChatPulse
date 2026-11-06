using CSharpFunctionalExtensions;

namespace ChatPulse.Shared.Abstractions;

public interface IQueryHandler<TResponse, in TQuery>
    where TQuery : IQuery
{
    Task<Result<TResponse, Error>> Handle(
        TQuery query,
        CancellationToken cancellationToken);
}