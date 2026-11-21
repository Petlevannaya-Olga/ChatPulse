using System.Text.RegularExpressions;

namespace ChatPulse.Application.Features.Chats.Analyze;

/// <summary>Одно исходное Telegram-сообщение. Его идентификатор не заменяется идентификатором ссылки.</summary>
public sealed record ChatActivityMessage(long MessageId, string? Text);

public enum ChatMessageClassification
{
    NOT_TASK,
    POSSIBLE_TASK,
    TASK
}

public enum ChatActivityClassification
{
    INSUFFICIENT_DATA,
    POSSIBLE_ACTIVITY,
    CONFLICTING_NETWORKS
}

public enum ChatTaskNetwork
{
    INSTAGRAM,
    TELEGRAM
}

public sealed record ChatMessageEvidence(long MessageId, ChatMessageClassification Classification,
    ChatTaskNetwork? Network, IReadOnlyList<string> PostLinks);

public sealed record ChatActivityAnalysis(ChatActivityClassification Classification,
    ChatTaskNetwork? Network, int TaskCount, int PossibleTaskCount,
    IReadOnlyList<ChatMessageEvidence> Messages);

/// <summary>
/// Локальная эвристика для предварительной проверки чатов. Никаких сетевых запросов,
/// сохранения настроек или автоматического подтверждения подключения.
/// </summary>
public static class ChatActivityAnalyzer
{
    private static readonly TimeSpan REGEX_TIMEOUT = TimeSpan.FromMilliseconds(500);

    private static readonly Regex LINK_PATTERN = new(
        @"https?://(?:www\.)?(?:instagram\.com|t\.me)/[^\s<>\[\]()]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, REGEX_TIMEOUT);

    private static readonly Regex ACTIVITY_WORDS = new(
        @"(?:лайк|лайки|реакци\w*|просмотр\w*|коммент\w*|репост\w*|сохран\w*|поддерж\w*|актив\w*|подпис\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, REGEX_TIMEOUT);

    public static ChatActivityAnalysis Analyze(IEnumerable<ChatActivityMessage> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var evidence = source.Select(AnalyzeMessage).ToArray();
        var tasks = evidence.Where(message => message.Classification == ChatMessageClassification.TASK).ToArray();
        var networks = tasks.Select(message => message.Network).Distinct().ToArray();

        // Смешанные задания не классифицируем как один тип соцсети.
        if (networks.Length > 1)
            return new ChatActivityAnalysis(ChatActivityClassification.CONFLICTING_NETWORKS, null,
                tasks.Length, evidence.Count(message => message.Classification == ChatMessageClassification.POSSIBLE_TASK), evidence);

        // Одна или несколько реальных просьб об активности: предварительный результат.
        // Подтверждение всё равно остаётся за пользователем.
        var classification = tasks.Length > 0
            ? ChatActivityClassification.POSSIBLE_ACTIVITY
            : ChatActivityClassification.INSUFFICIENT_DATA;
        return new ChatActivityAnalysis(classification, networks.Length == 1 ? networks[0] : null,
            tasks.Length, evidence.Count(message => message.Classification == ChatMessageClassification.POSSIBLE_TASK), evidence);
    }

    /// <summary>
    /// A standalone "Это наш ПОСТ ЧАТ" heading marks an administrative announcement,
    /// even when the message also contains publication URLs. A passing mention of
    /// the words inside a task sentence must not hide the task.
    /// </summary>
    public static bool HasPostChatRulesHeading(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var line in text.Split('\n'))
        {
            // Telegram may decorate the heading with emoji, punctuation and Markdown bold.
            var heading = new string(line.Where(character =>
                char.IsLetterOrDigit(character) || char.IsWhiteSpace(character) || character == '-').ToArray());
            var normalized = string.Join(' ', heading.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
            if (normalized.Equals("Это наш ПОСТ ЧАТ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Это наш ПОСТ-ЧАТ", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Это наш ПОСТЧАТ", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static ChatMessageEvidence AnalyzeMessage(ChatActivityMessage message)
    {
        var text = message.Text ?? string.Empty;
        if (HasPostChatRulesHeading(text))
            return new ChatMessageEvidence(message.MessageId, ChatMessageClassification.NOT_TASK,
                null, Array.Empty<string>());
        try
        {
            var recognized = new List<(ChatTaskNetwork Network, string Url)>();
            foreach (Match match in LINK_PATTERN.Matches(text))
            {
                var candidate = match.Value.TrimEnd('.', ',', '!', '?', ':', ';');
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
                if (IsInstagramPost(uri)) recognized.Add((ChatTaskNetwork.INSTAGRAM, candidate));
                else if (IsTelegramPost(uri)) recognized.Add((ChatTaskNetwork.TELEGRAM, candidate));
            }

            var networks = recognized.Select(link => link.Network).Distinct().ToArray();
            if (networks.Length != 1)
                return new ChatMessageEvidence(message.MessageId,
                    networks.Length > 1 ? ChatMessageClassification.POSSIBLE_TASK : ChatMessageClassification.NOT_TASK,
                    null, recognized.Select(link => link.Url).ToArray());

            var classification = ACTIVITY_WORDS.IsMatch(text)
                ? ChatMessageClassification.TASK
                : ChatMessageClassification.POSSIBLE_TASK;
            return new ChatMessageEvidence(message.MessageId, classification, networks[0],
                recognized.Select(link => link.Url).ToArray());
        }
        catch (RegexMatchTimeoutException)
        {
            // Не делаем вывод о сети или наличии задания, если входной текст не удалось обработать.
            return new ChatMessageEvidence(message.MessageId, ChatMessageClassification.POSSIBLE_TASK,
                null, Array.Empty<string>());
        }
    }

    private static bool IsInstagramPost(Uri uri)
    {
        if (!uri.Host.Equals("instagram.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("www.instagram.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 &&
               (segments[0].Equals("p", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("reel", StringComparison.OrdinalIgnoreCase) ||
                segments[0].Equals("tv", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTelegramPost(Uri uri)
    {
        if (!uri.Host.Equals("t.me", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 2 && long.TryParse(segments[1], out var postId) && postId > 0)
            return !segments[0].Equals("joinchat", StringComparison.OrdinalIgnoreCase);
        // Private-channel links: t.me/c/<channel-id>/<message-id>
        return segments.Length == 3 && segments[0].Equals("c", StringComparison.OrdinalIgnoreCase) &&
               long.TryParse(segments[1], out var channelId) && channelId > 0 &&
               long.TryParse(segments[2], out var messageId) && messageId > 0;
    }
}
