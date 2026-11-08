namespace ChatPulse.Contracts.Telegram;

/// <summary>Доступная группа или канал Telegram. Без access_hash и содержимого сообщений.</summary>
public sealed record TelegramChatOption(long PeerId, string PeerType, string Title, string Kind)
{
    public string DisplayName => $"{Title}  ·  {Kind}";
}
