namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Matches actual Telegram sender usernames, not message text or display names.
/// If Telegram does not expose either username, ownership is inconclusive.
/// </summary>
public static class TelegramOwnPostMatch
{
    public static bool? Compare(string? senderUsername, string? authorizedUsername)
    {
        if (string.IsNullOrWhiteSpace(senderUsername) || string.IsNullOrWhiteSpace(authorizedUsername))
            return null;
        return string.Equals(Normalize(senderUsername), Normalize(authorizedUsername),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Trim().TrimStart('@');
}
