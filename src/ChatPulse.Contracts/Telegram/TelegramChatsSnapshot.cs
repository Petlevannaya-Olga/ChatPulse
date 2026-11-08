namespace ChatPulse.Contracts.Telegram;

/// <summary>Чаты и идентификатор Telegram-аккаунта, от которого получен список.</summary>
public sealed record TelegramChatsSnapshot(long TelegramUserId, IReadOnlyList<TelegramChatOption> Chats);
