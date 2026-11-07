using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ResetSession;

/// <summary>Локально удалить авторизованную Telegram-сессию, сохранив API-ключи.</summary>
public sealed record ResetTelegramSessionCommand : ICommand;
