namespace ChatPulse.Contracts.Telegram;

/// <summary>Message text and optional evidence that the authorized account reacted to it.</summary>
public sealed record TelegramChatMessage(long MessageId, string? Text, bool? HasOwnReaction = null);
