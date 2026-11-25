namespace ChatPulse.Contracts.Telegram;

/// <summary>Message text and optional evidence that the authorized account reacted to it.</summary>
public sealed record TelegramChatMessage(long MessageId, string? Text, bool? HasOwnReaction = null, string? AuthorName = null, IReadOnlyList<TelegramCustomEmoji>? CustomEmojis = null, DateTime? SentAtUtc = null, string? AuthorUsername = null, bool? IsOwnAuthor = null);

public sealed record TelegramCustomEmoji(int Offset, int Length, long DocumentId);
