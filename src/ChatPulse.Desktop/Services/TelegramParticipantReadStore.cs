using System.IO;
using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// Reads the current Telegram membership register without involving Instagram checks.
/// A t.me/+... invite is a link, not a permanent Telegram user identity.
/// </summary>
public sealed record TelegramParticipantRow(int? Position, string DisplayName,
    string Category, string ProfileUrl, string Status, string? ResolutionNote = null)
{
    public string PositionLabel => Position?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    public string StatusLabel => Status switch
    {
        "PENALTY" => "Штрафной день",
        "WEEKEND" => "Выходные",
        _ => "В составе"
    };
    public string ProfileLabel => Uri.TryCreate(ProfileUrl, UriKind.Absolute, out var uri)
        ? uri.AbsolutePath.StartsWith("/+", StringComparison.Ordinal)
            ? "Пригласительная ссылка" : "@" + uri.AbsolutePath.Trim('/')
        : ProfileUrl;
}

public sealed class TelegramParticipantReadStore(string databasePath)
{
    public async Task<IReadOnlyList<TelegramParticipantRow>> ReadCurrentAsync(
        ConfiguredChat chat, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(databasePath)) return [];
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        using (var table = connection.CreateCommand())
        {
            table.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='ChatParticipants'";
            if (await table.ExecuteScalarAsync(cancellationToken) is null) return [];
        }
        // Backwards-compatible read before the next daily sync migrates older DBs.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(ChatParticipants)";
        var hasResolutionNote = false;
        await using (var columns = await pragma.ExecuteReaderAsync(cancellationToken))
            while (await columns.ReadAsync(cancellationToken))
                if (string.Equals(columns.GetString(1), "ResolutionNote", StringComparison.OrdinalIgnoreCase))
                    hasResolutionNote = true;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Position, DisplayName, Category, SubscriptionProfileUrl, Status,
                   {(hasResolutionNote ? "ResolutionNote" : "NULL AS ResolutionNote")}
            FROM ChatParticipants
            WHERE TelegramUserId=$owner AND PeerType=$type AND PeerId=$peer AND IsCurrent=1
            ORDER BY CASE WHEN Position IS NULL THEN 0 ELSE 1 END, Position, ParticipantId
            """;
        command.Parameters.AddWithValue("$owner", chat.TelegramUserId);
        command.Parameters.AddWithValue("$type", chat.PeerType);
        command.Parameters.AddWithValue("$peer", chat.PeerId);
        var result = new List<TelegramParticipantRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new TelegramParticipantRow(reader.IsDBNull(0) ? null : reader.GetInt32(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }
}
