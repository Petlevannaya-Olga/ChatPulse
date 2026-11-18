namespace ChatPulse.Desktop.Settings;

/// <summary>Имя Instagram без @, URL и чувствительных данных.</summary>
public static class InstagramUserName
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var name = value.Trim().TrimStart('@');
        if (name.Length is < 1 or > 30 || name[0] == '.' || name[^1] == '.' ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Any(character => !(character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '_' or '.')))
            throw new ArgumentException("Укажите имя Instagram без ссылки, пробелов и специальных символов.", nameof(value));
        return name.ToLowerInvariant();
    }
}
