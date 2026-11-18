using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>Activity account receives attention; subscription account is the one to follow.</summary>
public sealed record InstagramParticipantOption(long ParticipantId, string Label,
    string ActivityProfileUrl, string UserName, string Category = "Обычный",
    string SubscriptionProfileUrl = "", string DisplayName = "");

public static class InstagramActivityProfile
{
    public static string? TryGetUserName(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl) ||
            !Uri.TryCreate(profileUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") ||
            uri.Host is not ("instagram.com" or "www.instagram.com")) return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1 || segments[0] is "p" or "reel" or "stories" or "explore" or
            "accounts" or "direct" or "tv") return null;
        try { return InstagramUserName.Normalize(Uri.UnescapeDataString(segments[0])); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>VIPs and the administrative team are exempt only from the followers direction.</summary>
public static class InstagramBatchEligibility
{
    public static bool IsExcluded(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        return category.Contains("VIP", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("ВИП", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("АДМИН", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("МОДЕРАТОР", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("ОСНОВАТЕЛ", StringComparison.OrdinalIgnoreCase) ||
               category.Contains("ДИРЕКТОР", StringComparison.OrdinalIgnoreCase);
    }

    public static InstagramBatchSelection Prepare(IEnumerable<InstagramParticipantOption> current,
        IReadOnlySet<string>? ownUserNames = null)
    {
        var all = current.ToArray();
        var participants = ownUserNames is null ? all : all.Where(item =>
            !InstagramOwnAccountFilter.IsOwnParticipant(item, ownUserNames)).ToArray();
        var ownExcluded = all.Length - participants.Length;
        var excluded = participants.Count(item => IsExcluded(item.Category));
        var missingActivity = participants.Count(item => string.IsNullOrWhiteSpace(item.UserName));
        var missingSubscription = participants.Count(item => string.IsNullOrWhiteSpace(
            InstagramActivityProfile.TryGetUserName(item.SubscriptionProfileUrl)));
        // A VIP/admin's activity account is excluded from checking whether she follows me.
        // The account I must follow is taken independently from SubscriptionProfileUrl.
        var privileged = participants.Where(item => IsExcluded(item.Category) &&
                !string.IsNullOrWhiteSpace(item.UserName))
            .Select(item => item.UserName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activityNames = participants.Where(item => !string.IsNullOrWhiteSpace(item.UserName))
            .Select(item => item.UserName).ToArray();
        var subscriptionNames = participants
            .Select(item => InstagramActivityProfile.TryGetUserName(item.SubscriptionProfileUrl))
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!).ToArray();
        var following = subscriptionNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var followers = activityNames.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(userName => !privileged.Contains(userName)).ToArray();
        return new InstagramBatchSelection(followers, following, excluded, missingActivity,
            activityNames.Length - activityNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ownExcluded, missingSubscription);
    }
}

public sealed record InstagramBatchSelection(IReadOnlyList<string> FollowsMeUserNames,
    IReadOnlyList<string> IFollowUserNames, int ExcludedCount,
    int MissingActivityProfileCount, int DuplicateCount, int OwnAccountsExcludedCount = 0,
    int MissingSubscriptionProfileCount = 0)
{
    // Compatibility with callers that use the regular participants list.
    public IReadOnlyList<string> UserNames => FollowsMeUserNames;
    public int SearchCount => FollowsMeUserNames.Count + IFollowUserNames.Count;
}

/// <summary>Reads the current chat-local registers, not historical participants.</summary>
public sealed class InstagramParticipantLookup(string databasePath)
{
    public async Task<IReadOnlyList<InstagramParticipantOption>> ReadCurrentAsync(
        IEnumerable<ConfiguredChat> chats, CancellationToken cancellationToken = default)
        => (await ReadAllCurrentAsync(chats, cancellationToken))
            .Where(item => !string.IsNullOrWhiteSpace(item.UserName)).ToArray();

    public async Task<IReadOnlyList<InstagramParticipantOption>> ReadAllCurrentAsync(
        IEnumerable<ConfiguredChat> chats, CancellationToken cancellationToken = default)
    {
        var instagramChats = chats.Where(chat =>
            string.Equals(chat.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) &&
            chat.SynchronizationState == ChatSynchronizationState.COMPLETED).ToArray();
        if (!File.Exists(databasePath) || instagramChats.Length == 0)
            return [];

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ChatParticipants'";
        if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            return [];

        var members = new List<InstagramParticipantOption>();
        foreach (var chat in instagramChats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ParticipantId, Position, DisplayName, ActivityProfileUrl, Category, SubscriptionProfileUrl
                FROM ChatParticipants
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                    AND IsCurrent=1
                ORDER BY Position, ParticipantId
                """;
            command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            command.Parameters.AddWithValue("$type", chat.PeerType);
            command.Parameters.AddWithValue("$peer", chat.PeerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var url = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                var name = InstagramActivityProfile.TryGetUserName(url) ?? string.Empty;
                var position = reader.IsDBNull(1) ? "" : $"№{reader.GetInt32(1)} · ";
                var display = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var label = $"{chat.TelegramTitle}: {position}" +
                    (string.IsNullOrWhiteSpace(display) ? $"@{name}" : $"{display} (@{name})");
                members.Add(new InstagramParticipantOption(reader.GetInt64(0), label, url, name,
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5), display));
            }
        }
        return members;
    }
}
