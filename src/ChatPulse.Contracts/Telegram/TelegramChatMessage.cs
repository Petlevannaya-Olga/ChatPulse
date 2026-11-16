namespace ChatPulse.Contracts.Telegram;

/// <summary>Only the text needed for local activity detection; no message data is persisted.</summary>
public sealed record TelegramChatMessage(long MessageId, string? Text);
