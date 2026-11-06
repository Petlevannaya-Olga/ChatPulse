using CSharpFunctionalExtensions;

namespace ChatPulse.Shared.Abstractions;

public interface ICommandHandler<TResponse, in TCommand>
    where TCommand : ICommand
{
    Task<Result<TResponse, Error>> Handle(
        TCommand command,
        CancellationToken cancellationToken);
}