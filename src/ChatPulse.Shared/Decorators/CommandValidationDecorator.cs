using CSharpFunctionalExtensions;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;
using ChatPulse.Shared.Validation;

namespace ChatPulse.Shared.Decorators;

public sealed class CommandValidationDecorator<TResponse, TCommand>(
    ICommandHandler<TResponse, TCommand> inner,
    ValidationExecutor<TCommand> validator)
    : ICommandHandler<TResponse, TCommand>
    where TCommand : ICommand
{
    public Task<Result<TResponse, Error>> Handle(
        TCommand command,
        CancellationToken cancellationToken)
    {
        return validator.ExecuteAsync(
            command,
            token => inner.Handle(command, token),
            cancellationToken);
    }
}