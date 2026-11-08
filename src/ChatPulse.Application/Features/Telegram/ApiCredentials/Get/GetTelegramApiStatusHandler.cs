using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Get;

public sealed class GetTelegramApiStatusHandler(ITelegramApiCredentialsStore store, ITelegramOperationGate operationGate)
    : IQueryHandler<bool, GetTelegramApiStatusQuery>
{
    public async Task<Result<bool, Error>> Handle(
        GetTelegramApiStatusQuery query, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        return Result.Success<bool, Error>(await store.IsConfiguredAsync(cancellationToken));
    }
}
