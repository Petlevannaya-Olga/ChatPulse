using ChatPulse.Application.Features.Chats.Analyze;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Identifies a post-chat rules announcement (ChatRules), not a task.
/// An account URL or an individual instruction is insufficient evidence.
/// The original imported message remains in SQLite.
/// </summary>
public static class ChatRulesMessageClassifier
{
    public static bool IsChatRules(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // The standalone heading is sufficient: a short rules announcement may
        // contain post links without the full set of instructions.
        if (ChatActivityAnalyzer.HasPostChatRulesHeading(text)) return true;

        var hasHeading = ContainsAny(text,
            "это наш пост чат", "это наш пост-чат", "правила постчата",
            "правила пост-чата", "правила пост чата", "правила нашего постчата");

        var instructions = 0;
        if (ContainsAny(text, "скидываем ссылки", "присылаем ссылки", "ссылки для актива")) instructions++;
        if (ContainsAny(text, "обязательно 2 ссылки", "обязательно две ссылки", "минимум 2 ссылки")) instructions++;
        if (ContainsAny(text, "лишнего ничего не пишем", "только просьбы актива", "только просьбы об активе")) instructions++;
        if (ContainsAny(text, "не более 4х ссылок", "не более 4 ссылок", "ссылок в неделю")) instructions++;
        if (ContainsAny(text, "пост чат закрывается", "пост-чат закрывается", "постчат закрывается",
                "пост чат открывается", "пост-чат открывается", "постчат открывается")) instructions++;
        if (ContainsAny(text, "суббота и воскресенье выходные", "выходные в чате",
                "выходные в постчате", "выходные в пост чате")) instructions++;
        if (ContainsAny(text, "аккаунт для репостов", "профиль для репостов")) instructions++;

        // Without a distinctive heading demand multiple independent instructions.
        // A normal task can contain a permanent account link and one instruction.
        return hasHeading ? instructions >= 2 : instructions >= 4;
    }

    private static bool ContainsAny(string text, params string[] phrases)
        => phrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
}
