using System.IO;
using System.Security.Cryptography;
using System.Text;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// A successful bulk check establishes a baseline of checkable Instagram profiles.
/// Daily Telegram synchronization can make that baseline stale, but an individual
/// check, an interrupted bulk check and a repeated list must not clear the warning.
/// </summary>
public sealed record InstagramReviewFingerprint(long TelegramUserId, string PeerType,
    long PeerId, InstagramCheckDirections Direction, string Fingerprint);

public sealed class InstagramSubscriptionReviewStateStore(string databasePath)
{
    private const string CREATE = """
        CREATE TABLE IF NOT EXISTS InstagramSubscriptionReviewStates (
            TargetUserName TEXT NOT NULL COLLATE NOCASE,
            TelegramUserId INTEGER NOT NULL,
            PeerType TEXT NOT NULL,
            PeerId INTEGER NOT NULL,
            Direction INTEGER NOT NULL CHECK(Direction IN (1, 2)),
            CheckedFingerprint TEXT,
            PendingWithoutBaseline INTEGER NOT NULL DEFAULT 0
                CHECK(PendingWithoutBaseline IN (0, 1)),
            PRIMARY KEY(TargetUserName, TelegramUserId, PeerType, PeerId, Direction)
        );
        """;

    public async Task<IReadOnlyList<InstagramReviewFingerprint>> CaptureAsync(
        IReadOnlyList<ConfiguredChat> chats, IReadOnlySet<string> ownNames,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return Capture(connection, null, chats, ownNames);
    }

    public async Task<bool> NeedsReviewAsync(string? targetUserName, ConfiguredChat selectedChat,
        IReadOnlyList<ConfiguredChat> chats, IReadOnlySet<string> ownNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetUserName) || !File.Exists(databasePath)) return false;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        if (!HasTable(connection, null)) return false;
        var current = Capture(connection, null, chats, ownNames)
            .Where(item => SameChat(item, selectedChat)).ToArray();
        foreach (var fingerprint in current)
        {
            using var command = Command(connection, null, """
                SELECT CheckedFingerprint, PendingWithoutBaseline
                FROM InstagramSubscriptionReviewStates
                WHERE TargetUserName=$target AND TelegramUserId=$owner
                    AND PeerType=$type AND PeerId=$peer AND Direction=$direction
                """);
            AddKey(command, targetUserName, fingerprint);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            if (!reader.IsDBNull(0))
            {
                if (!string.Equals(reader.GetString(0), fingerprint.Fingerprint,
                        StringComparison.Ordinal)) return true;
            }
            else if (reader.GetInt64(1) != 0) return true;
        }
        return false;
    }

    /// <summary>Called inside the SAME transaction that publishes a completed bulk run.</summary>
    public static void PublishBaselines(SqliteConnection connection, SqliteTransaction transaction,
        string targetUserName, InstagramCheckDirections directions,
        IReadOnlyList<InstagramReviewFingerprint> fingerprints)
    {
        EnsureSchema(connection, transaction);
        foreach (var fingerprint in fingerprints.Where(item => (directions & item.Direction) != 0))
        {
            using var command = Command(connection, transaction, """
                INSERT INTO InstagramSubscriptionReviewStates
                    (TargetUserName, TelegramUserId, PeerType, PeerId, Direction,
                     CheckedFingerprint, PendingWithoutBaseline)
                VALUES ($target, $owner, $type, $peer, $direction, $fingerprint, 0)
                ON CONFLICT(TargetUserName, TelegramUserId, PeerType, PeerId, Direction)
                DO UPDATE SET CheckedFingerprint=excluded.CheckedFingerprint,
                    PendingWithoutBaseline=0
                """);
            AddKey(command, targetUserName, fingerprint);
            command.Parameters.AddWithValue("$fingerprint", fingerprint.Fingerprint);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// For databases predating the fingerprint feature there is no reliable baseline.
    /// Preserve an observed roster change until a full bulk check establishes one.
    /// With an established baseline the UI compares fingerprints, allowing a reverted
    /// roster to stop requiring a check without another Instagram operation.
    /// </summary>
    public static void RecordChanges(SqliteConnection connection, SqliteTransaction transaction,
        string? targetUserName, IReadOnlyList<InstagramReviewFingerprint> before,
        IReadOnlyList<InstagramReviewFingerprint> after)
    {
        if (string.IsNullOrWhiteSpace(targetUserName)) return;
        var old = before.ToDictionary(Key);
        EnsureSchema(connection, transaction);
        foreach (var fingerprint in after)
        {
            if (old.TryGetValue(Key(fingerprint), out var prior) &&
                string.Equals(prior.Fingerprint, fingerprint.Fingerprint, StringComparison.Ordinal))
                continue;
            using var command = Command(connection, transaction, """
                INSERT INTO InstagramSubscriptionReviewStates
                    (TargetUserName, TelegramUserId, PeerType, PeerId, Direction,
                     CheckedFingerprint, PendingWithoutBaseline)
                VALUES ($target, $owner, $type, $peer, $direction, NULL, 1)
                ON CONFLICT(TargetUserName, TelegramUserId, PeerType, PeerId, Direction)
                DO UPDATE SET PendingWithoutBaseline =
                    CASE WHEN CheckedFingerprint IS NULL THEN 1
                         ELSE PendingWithoutBaseline END
                """);
            AddKey(command, targetUserName, fingerprint);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Same membership and eligibility rules as the Instagram bulk-check plan.</summary>
    public static IReadOnlyList<InstagramReviewFingerprint> Capture(SqliteConnection connection,
        SqliteTransaction? transaction, IReadOnlyList<ConfiguredChat> chats,
        IReadOnlySet<string> ownNames)
    {
        var rows = new List<(ConfiguredChat Chat, string? Subscription, string? Activity, string Category)>();
        if (!HasTable(connection, transaction, "ChatParticipants")) return [];
        foreach (var chat in chats.Where(item =>
                     string.Equals(item.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) &&
                     item.SynchronizationState == ChatSynchronizationState.COMPLETED))
        {
            using var command = Command(connection, transaction, """
                SELECT SubscriptionProfileUrl, ActivityProfileUrl, Category
                FROM ChatParticipants
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer AND IsCurrent=1
                """);
            command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
            command.Parameters.AddWithValue("$type", chat.PeerType);
            command.Parameters.AddWithValue("$peer", chat.PeerId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add((chat,
                    InstagramActivityProfile.TryGetUserName(reader.GetString(0)),
                    InstagramActivityProfile.TryGetUserName(reader.GetString(1)),
                    reader.GetString(2)));
        }
        var privileged = rows.Where(row => InstagramBatchEligibility.IsExcluded(row.Category) &&
                                           row.Activity is not null)
            .Select(row => row.Activity!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstagramReviewFingerprint>();
        foreach (var chat in chats.Where(item =>
                     string.Equals(item.SocialNetwork, "Instagram", StringComparison.OrdinalIgnoreCase) &&
                     item.SynchronizationState == ChatSynchronizationState.COMPLETED))
        {
            var following = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var followers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows.Where(row => row.Chat.TelegramUserId == chat.TelegramUserId &&
                                                  row.Chat.PeerId == chat.PeerId &&
                                                  row.Chat.PeerType == chat.PeerType))
            {
                if ((row.Subscription is not null && ownNames.Contains(row.Subscription)) ||
                    (row.Activity is not null && ownNames.Contains(row.Activity))) continue;
                // Subscription name is the chat-local participant identity.
                var identity = row.Subscription ?? string.Empty;
                if (row.Subscription is not null)
                    following.Add(row.Subscription.ToLowerInvariant());
                if (row.Activity is not null && !privileged.Contains(row.Activity))
                    followers.Add(identity.ToLowerInvariant() + "|" + row.Activity.ToLowerInvariant());
            }
            result.Add(new InstagramReviewFingerprint(chat.TelegramUserId, chat.PeerType,
                chat.PeerId, InstagramCheckDirections.FOLLOWING, Hash(following)));
            result.Add(new InstagramReviewFingerprint(chat.TelegramUserId, chat.PeerType,
                chat.PeerId, InstagramCheckDirections.FOLLOWERS, Hash(followers)));
        }
        return result;
    }

    internal static IReadOnlySet<string> ReadOwnNames(SqliteConnection connection,
        SqliteTransaction? transaction, string? target, string? browser)
    {
        var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target)) own.Add(target.Trim().TrimStart('@'));
        if (!string.IsNullOrWhiteSpace(browser)) own.Add(browser.Trim().TrimStart('@'));
        if (!HasTable(connection, transaction, "InstagramOwnAccounts")) return own;
        using var command = Command(connection, transaction, "SELECT UserName FROM InstagramOwnAccounts");
        using var reader = command.ExecuteReader();
        while (reader.Read()) own.Add(reader.GetString(0));
        return own;
    }

    private static string Hash(IEnumerable<string> names) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join("\n", names.OrderBy(name => name, StringComparer.Ordinal)))));

    private static (long Owner, string Type, long Peer, InstagramCheckDirections Direction) Key(
        InstagramReviewFingerprint fingerprint) =>
        (fingerprint.TelegramUserId, fingerprint.PeerType, fingerprint.PeerId, fingerprint.Direction);

    private static bool SameChat(InstagramReviewFingerprint item, ConfiguredChat chat) =>
        item.TelegramUserId == chat.TelegramUserId && item.PeerId == chat.PeerId &&
        string.Equals(item.PeerType, chat.PeerType, StringComparison.Ordinal);

    private static void AddKey(SqliteCommand command, string targetUserName,
        InstagramReviewFingerprint fingerprint)
    {
        command.Parameters.AddWithValue("$target", targetUserName.Trim().TrimStart('@'));
        command.Parameters.AddWithValue("$owner", fingerprint.TelegramUserId);
        command.Parameters.AddWithValue("$type", fingerprint.PeerType);
        command.Parameters.AddWithValue("$peer", fingerprint.PeerId);
        command.Parameters.AddWithValue("$direction", (int)fingerprint.Direction);
    }

    private static void EnsureSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, CREATE);
        command.ExecuteNonQuery();
    }

    private static bool HasTable(SqliteConnection connection, SqliteTransaction? transaction,
        string table = "InstagramSubscriptionReviewStates")
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name");
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static SqliteCommand Command(SqliteConnection connection,
        SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
