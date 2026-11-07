using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.RestoreSession;

/// <summary>Проверить сохранённую авторизацию Telegram, не запрашивая код входа.</summary>
public sealed record RestoreTelegramSessionQuery : IQuery;
