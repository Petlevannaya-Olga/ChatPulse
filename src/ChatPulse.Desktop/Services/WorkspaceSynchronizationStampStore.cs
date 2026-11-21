using Microsoft.Data.Sqlite;
using ChatPulse.Desktop.Settings;

namespace ChatPulse.Desktop.Services;

/// <summary>Successful user-initiated synchronization moments, independent per chat and globally.</summary>
public sealed class WorkspaceSynchronizationStampStore(string databasePath)
{
    private const string TABLE_SQL = """
        CREATE TABLE IF NOT EXISTS WorkspaceSynchronizationStamps (
            Scope TEXT NOT NULL,
            TelegramUserId INTEGER NOT NULL,
            PeerType TEXT NOT NULL,
            PeerId INTEGER NOT NULL,
            CompletedAtUtc TEXT NOT NULL,
            PRIMARY KEY (Scope, TelegramUserId, PeerType, PeerId)
        )
        """;

    public async Task RecordChatSuccessAsync(ConfiguredChat chat, DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
        => await WriteAsync("chat", chat.TelegramUserId, chat.PeerType, chat.PeerId, timestamp, cancellationToken);

    public async Task RecordAllSuccessAsync(long owner, DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
        => await WriteAsync("all", owner, string.Empty, 0, timestamp, cancellationToken);

    public Task<DateTimeOffset?> ReadChatAsync(ConfiguredChat chat,
        CancellationToken cancellationToken = default)
        => ReadAsync("chat", chat.TelegramUserId, chat.PeerType, chat.PeerId, cancellationToken);

    public Task<DateTimeOffset?> ReadAllAsync(long owner, CancellationToken cancellationToken = default)
        => ReadAsync("all", owner, string.Empty, 0, cancellationToken);

    private async Task WriteAsync(string scope, long owner, string peerType, long peerId,
        DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = TABLE_SQL;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WorkspaceSynchronizationStamps(Scope, TelegramUserId, PeerType, PeerId, CompletedAtUtc)
            VALUES ($scope, $owner, $peerType, $peerId, $timestamp)
            ON CONFLICT(Scope, TelegramUserId, PeerType, PeerId)
            DO UPDATE SET CompletedAtUtc=excluded.CompletedAtUtc
            """;
        SetParameters(command, scope, owner, peerType, peerId);
        command.Parameters.AddWithValue("$timestamp", timestamp.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<DateTimeOffset?> ReadAsync(string scope, long owner, string peerType, long peerId,
        CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(databasePath)) return null;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkspaceSynchronizationStamps'";
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return null;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CompletedAtUtc FROM WorkspaceSynchronizationStamps
            WHERE Scope=$scope AND TelegramUserId=$owner AND PeerType=$peerType AND PeerId=$peerId
            """;
        SetParameters(command, scope, owner, peerType, peerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var date) ? date : null;
    }

    private SqliteConnection CreateConnection() => new(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString());

    private static void SetParameters(SqliteCommand command, string scope, long owner,
        string peerType, long peerId)
    {
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$peerType", peerType);
        command.Parameters.AddWithValue("$peerId", peerId);
    }
}
