using ChatPulse.Application.Services;

namespace ChatPulse.Application.Features.Telegram.ApiCredentials;

internal static class TelegramApiCredentialsComparer
{
    // API Hash is hexadecimal: letter case does not change the key.
    public static bool AreSame(TelegramApiCredentials? saved, int apiId, string apiHash)
        => saved is not null && saved.ApiId == apiId &&
           string.Equals(saved.ApiHash, apiHash, StringComparison.OrdinalIgnoreCase);
}
