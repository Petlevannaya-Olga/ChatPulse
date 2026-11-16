namespace ChatPulse.Desktop.Settings;

/// <summary>
/// Фактический этап мастера настройки. Сохраняется между запусками.
/// </summary>
public enum OnboardingStep
{
    Welcome,
    Profile,
    Telegram,
    FirstChat,
    Synchronization,
}

/// <summary>
/// Подэтап подключения Telegram. Не содержит API-ключей или кодов авторизации.
/// </summary>
public enum TelegramOnboardingPage
{
    ApiCredentials,
    Phone,
}

/// <summary>
/// Только пользовательские настройки; коды, пароли, api_hash и MTProto-сессия здесь не хранятся.
/// </summary>
public sealed class AppSettings
{
    public string? UserName { get; set; }

    public OnboardingStep CurrentOnboardingStep { get; set; } = OnboardingStep.Welcome;

    public TelegramOnboardingPage CurrentTelegramPage { get; set; } = TelegramOnboardingPage.ApiCredentials;

    public bool IsOnboardingCompleted { get; set; }

    /// <summary>Конфигурация подключённых чатов, не история заданий.</summary>
    public List<ConfiguredChat> Chats { get; set; } = [];

    /// <summary>Проверенные кандидаты шага 3; ещё не подключённые чаты.</summary>
    public List<PendingChatConnection> PendingChats { get; set; } = [];
}

/// <summary>Одна сохранённая настройка чата. Дата трактуется как 00:00 в часовом поясе чата при импорте.</summary>
public sealed class ConfiguredChat
{
    /// <summary>Владелец настроек. 0 = старая запись без привязки, использовать автоматически нельзя.</summary>
    public long TelegramUserId { get; set; }
    public long PeerId { get; set; }
    public string PeerType { get; set; } = string.Empty;
    public string TelegramTitle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SocialNetwork { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; } // Legacy configuration; not used for initial synchronization.
    public int HistoryDays { get; set; } = 7;
    public ChatSynchronizationState SynchronizationState { get; set; } = ChatSynchronizationState.PENDING;
    public int ImportedMessageCount { get; set; }
    public string? SynchronizationError { get; set; }
    public long? LastProcessedMessageId { get; set; }
}

public enum ChatSynchronizationState
{
    PENDING,
    RUNNING,
    FAILED,
    COMPLETED,
}


/// <summary>Намерение подключить чат. На шаге 4 анализ проверяется повторно перед сохранением.</summary>
public sealed class PendingChatConnection
{
    public long TelegramUserId { get; set; }
    public long PeerId { get; set; }
    public string PeerType { get; set; } = string.Empty;
    public string TelegramTitle { get; set; } = string.Empty;
    public string SocialNetwork { get; set; } = string.Empty;
    public int HistoryDays { get; set; } = 7;
}
