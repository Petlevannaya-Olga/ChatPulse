using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials.Get;

/// <summary>Only returns whether submitted keys differ; never returns stored secrets.</summary>
public sealed record HaveTelegramApiCredentialsChangedQuery(string ApiId, string ApiHash) : IQuery;
