using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>Stores each recognized full participant register as a chat-local snapshot.</summary>
public sealed class ChatParticipantRegistry(string databasePath)
{
    public async Task<int> UpdateAsync(ConfiguredChat chat,
        IEnumerable<(long MessageId, string? Text)> messages,
        CancellationToken cancellationToken = default)
    {
        var snapshots = messages.OrderBy(item => item.MessageId).Select(item =>
        {
            var valid = ParticipantListParser.TryParse(item.Text, out var parsed);
            return (item.MessageId, Valid: valid, Parsed: parsed);
        }).Where(item => item.Valid && item.Parsed is not null).ToArray();
        if (snapshots.Length == 0) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS ChatParticipantRegistryVersions (
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                SourceMessageId INTEGER NOT NULL,
                PRIMARY KEY (TelegramUserId, PeerType, PeerId)
            );
            CREATE TABLE IF NOT EXISTS ChatParticipants (
                ParticipantId INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramUserId INTEGER NOT NULL, PeerType TEXT NOT NULL, PeerId INTEGER NOT NULL,
                Position INTEGER, DisplayName TEXT, SubscriptionProfileUrl TEXT NOT NULL,
                ActivityProfileUrl TEXT NOT NULL, Category TEXT NOT NULL,
                Status TEXT NOT NULL, IsCurrent INTEGER NOT NULL CHECK (IsCurrent IN (0, 1)),
                SourceMessageId INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ChatParticipants_Chat
                ON ChatParticipants(TelegramUserId, PeerType, PeerId);
            """;
        await setup.ExecuteNonQueryAsync(cancellationToken);
        var applied = 0;
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var version = connection.CreateCommand();
            version.Transaction = transaction;
            version.CommandText = """
                SELECT SourceMessageId FROM ChatParticipantRegistryVersions
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(version, chat);
            var prior = await version.ExecuteScalarAsync(cancellationToken);
            if (prior is not null && Convert.ToInt64(prior) > snapshot.MessageId)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = """
                SELECT ParticipantId, SubscriptionProfileUrl, ActivityProfileUrl, DisplayName
                FROM ChatParticipants
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(current, chat);
            var known = new List<(long Id, string Subscription, string Activity, string? Name)>();
            await using (var reader = await current.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                    known.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
            var matches = new List<(ParsedParticipant Participant, long? Id)>();
            var used = new HashSet<long>();
            var ambiguous = false;
            foreach (var participant in snapshot.Parsed!.Participants)
            {
                var bySubscription = known.Where(item => item.Subscription.Equals(
                    participant.SubscriptionProfileUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
                var byActivity = known.Where(item => item.Activity.Equals(
                    participant.ActivityProfileUrl, StringComparison.OrdinalIgnoreCase)).ToArray();
                var possible = bySubscription.Concat(byActivity).DistinctBy(item => item.Id).ToArray();
                // Shared links are not proof of identical people. Only a unique
                // display-name match may disambiguate people with the same URL.
                if (possible.Length > 1 && !string.IsNullOrWhiteSpace(participant.DisplayName))
                    possible = possible.Where(item => string.Equals(item.Name,
                        participant.DisplayName, StringComparison.OrdinalIgnoreCase)).ToArray();
                var candidates = possible.Select(item => item.Id).ToArray();
                if (candidates.Length > 1 || candidates.Length == 1 && !used.Add(candidates[0]))
                {
                    ambiguous = true;
                    break;
                }
                matches.Add((participant, candidates.Length == 1 ? candidates[0] : null));
            }
            // Never corrupt the previous register by guessing who changed accounts.
            if (ambiguous)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }
            var deactivate = connection.CreateCommand();
            deactivate.Transaction = transaction;
            deactivate.CommandText = """
                UPDATE ChatParticipants SET IsCurrent=0
                WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                """;
            AddChatParameters(deactivate, chat);
            await deactivate.ExecuteNonQueryAsync(cancellationToken);
            foreach (var (participant, id) in matches)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = id.HasValue ? """
                    UPDATE ChatParticipants SET Position=$position, DisplayName=$name,
                        SubscriptionProfileUrl=$subscription, ActivityProfileUrl=$activity,
                        Category=$category, Status=$status, IsCurrent=1, SourceMessageId=$source
                    WHERE ParticipantId=$id AND TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer
                    """ : """
                    INSERT INTO ChatParticipants (TelegramUserId, PeerType, PeerId, Position, DisplayName,
                        SubscriptionProfileUrl, ActivityProfileUrl, Category, Status, IsCurrent, SourceMessageId)
                    VALUES ($owner, $type, $peer, $position, $name, $subscription, $activity,
                        $category, $status, 1, $source)
                    """;
                AddChatParameters(command, chat);
                command.Parameters.AddWithValue("$position", participant.Position is int number ? (object)number : DBNull.Value);
                command.Parameters.AddWithValue("$name", (object?)participant.DisplayName ?? DBNull.Value);
                command.Parameters.AddWithValue("$subscription", participant.SubscriptionProfileUrl);
                command.Parameters.AddWithValue("$activity", participant.ActivityProfileUrl);
                command.Parameters.AddWithValue("$category", participant.Category);
                command.Parameters.AddWithValue("$status", participant.Status.ToString());
                command.Parameters.AddWithValue("$source", snapshot.MessageId);
                if (id.HasValue) command.Parameters.AddWithValue("$id", id.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            var saveVersion = connection.CreateCommand();
            saveVersion.Transaction = transaction;
            saveVersion.CommandText = """
                INSERT INTO ChatParticipantRegistryVersions
                    (TelegramUserId, PeerType, PeerId, SourceMessageId)
                VALUES ($owner, $type, $peer, $source)
                ON CONFLICT(TelegramUserId, PeerType, PeerId)
                DO UPDATE SET SourceMessageId=excluded.SourceMessageId
                """;
            AddChatParameters(saveVersion, chat);
            saveVersion.Parameters.AddWithValue("$source", snapshot.MessageId);
            await saveVersion.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            applied++;
        }
        return applied;
    }

    private static void AddChatParameters(SqliteCommand command, ConfiguredChat chat)
    {
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
    }
}
