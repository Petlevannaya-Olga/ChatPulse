using ChatPulse.Shared.Abstractions;

namespace ChatPulse.Application.Features.Telegram.ResendCode;

/// <summary>Повторно отправляет текущий код, если Telegram разрешил другой способ доставки.</summary>
public sealed record ResendTelegramCodeCommand : ICommand;
