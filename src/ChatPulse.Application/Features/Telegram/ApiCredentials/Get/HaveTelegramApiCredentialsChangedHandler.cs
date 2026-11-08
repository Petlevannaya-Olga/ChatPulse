using System.Globalization;
using CSharpFunctionalExtensions;
using ChatPulse.Application.Services;
using ChatPulse.Shared;
using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Get;

public sealed class HaveTelegramApiCredentialsChangedHandler(ITelegramApiCredentialsStore store,
    ITelegramOperationGate operationGate)
    : IQueryHandler<bool, HaveTelegramApiCredentialsChangedQuery>
{
    public async Task<Result<bool, Error>> Handle(
        HaveTelegramApiCredentialsChangedQuery query, CancellationToken cancellationToken)
    {
        using var operation = await operationGate.EnterAsync(cancellationToken);
        // The Save command is validated independently via FluentValidation.
        // Invalid draft input is not considered equal to any stored credentials.
        if (!int.TryParse(query.ApiId, NumberStyles.None, CultureInfo.InvariantCulture, out var apiId))
            return Result.Success<bool, Error>(true);

        var saved = await store.LoadAsync(cancellationToken);
        return Result.Success<bool, Error>(
            !TelegramApiCredentialsComparer.AreSame(saved, apiId, query.ApiHash));
    }
}
