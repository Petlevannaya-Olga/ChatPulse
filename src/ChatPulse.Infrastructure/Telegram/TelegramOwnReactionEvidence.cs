using System.Collections;
using System.Reflection;
using TL;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Telegram's reactionCount.chosen_order flag belongs to the authorized account.
/// The result is unknown for abbreviated or incompatible reaction payloads.
/// Does not inspect other users' reactions.
/// </summary>
internal static class TelegramOwnReactionEvidence
{
    public static bool? Read(Message message) => ReadEvidence(message);

    internal static bool? ReadEvidence(object message)
    {
        var reactions = GetMember(message, "reactions");
        if (reactions is null) return null;

        // A minimal MessageReactions does not carry the current user's choices.
        var min = GetMember(reactions, "min");
        if (min is true) return null;
        if (min is null && GetFlag(reactions, 0) is true) return null;

        if (GetMember(reactions, "results") is not IEnumerable results) return null;
        var foundSupportedCount = false;
        foreach (var count in results)
        {
            if (count is null) continue;
            var chosen = Chosen(count);
            if (chosen is true) return true;
            if (chosen is null) return null;
            foundSupportedCount = true;
        }
        // Empty full reaction lists also mean no current-account reaction.
        return foundSupportedCount || min is false || GetFlag(reactions, 0) is false ? false : null;
    }

    private static bool? Chosen(object count)
    {
        var chosen = GetMember(count, "chosen_order");
        if (chosen is null)
        {
            // When chosen_order is nullable, null means the optional flag is absent.
            var type = GetMemberType(count, "chosen_order");
            if (type is null) return null;
            return Nullable.GetUnderlyingType(type) is not null ? false : GetFlag(count, 0);
        }
        if (chosen is int)
        {
            var type = GetMemberType(count, "chosen_order");
            if (Nullable.GetUnderlyingType(type!) is not null) return true;
            return GetFlag(count, 0);
        }
        return null;
    }

    private static bool? GetFlag(object value, int bit)
    {
        var flags = GetMember(value, "flags") ?? GetMember(value, "Flags");
        if (flags is null) return null;
        try { return (Convert.ToInt32(flags) & (1 << bit)) != 0; }
        catch (Exception) { return null; }
    }

    private static object? GetMember(object obj, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var type = obj.GetType();
        return type.GetField(name, flags)?.GetValue(obj) ?? type.GetProperty(name, flags)?.GetValue(obj);
    }

    private static Type? GetMemberType(object obj, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
        var type = obj.GetType();
        return type.GetField(name, flags)?.FieldType ?? type.GetProperty(name, flags)?.PropertyType;
    }
}
