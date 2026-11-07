using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Get;

public sealed class GetTelegramApiStatusHandler(ITelegramApiCredentialsStore store)
    : IQueryHandler<bool, GetTelegramApiStatusQuery>
{
    public async Task<Result<bool, Error>> Handle(
        GetTelegramApiStatusQuery query, CancellationToken cancellationToken)
        => Result.Success<bool, Error>(await store.IsConfiguredAsync(cancellationToken));
}
