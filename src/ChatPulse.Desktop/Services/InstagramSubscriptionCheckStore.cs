using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>Persists each completed direction immediately; partial runs remain inspectable.</summary>
public sealed class InstagramSubscriptionCheckStore(string databasePath)
{
    public void Save(string targetUserName, string browserUserName, InstagramBatchProgress progress)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS InstagramSubscriptionChecks (
                TargetUserName TEXT NOT NULL COLLATE NOCASE,
                ParticipantUserName TEXT NOT NULL COLLATE NOCASE,
                Direction TEXT NOT NULL CHECK (Direction IN ('followers','following')),
                BrowserUserName TEXT NOT NULL,
                SearchStatus TEXT NOT NULL CHECK (SearchStatus IN ('FOUND','NOT_FOUND','INCONCLUSIVE')),
                Details TEXT NOT NULL,
                CheckedAtUtc TEXT NOT NULL,
                PRIMARY KEY (TargetUserName, ParticipantUserName, Direction)
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO InstagramSubscriptionChecks
                (TargetUserName, ParticipantUserName, Direction, BrowserUserName,
                 SearchStatus, Details, CheckedAtUtc)
            VALUES ($target, $participant, $direction, $browser, $status, $details, $checked)
            ON CONFLICT(TargetUserName, ParticipantUserName, Direction) DO UPDATE SET
                BrowserUserName=excluded.BrowserUserName,
                SearchStatus=excluded.SearchStatus,
                Details=excluded.Details,
                CheckedAtUtc=excluded.CheckedAtUtc
            """;
        command.Parameters.AddWithValue("$target", targetUserName);
        command.Parameters.AddWithValue("$participant", progress.UserName);
        command.Parameters.AddWithValue("$direction", progress.Kind);
        command.Parameters.AddWithValue("$browser", browserUserName);
        command.Parameters.AddWithValue("$status", progress.Result.Status.ToString());
        command.Parameters.AddWithValue("$details", progress.Result.Details);
        command.Parameters.AddWithValue("$checked", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<InstagramSavedCheck> Read(string targetUserName, IReadOnlyCollection<string> names)
    {
        if (!File.Exists(databasePath) || names.Count == 0) return [];
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        connection.Open();
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='InstagramSubscriptionChecks'";
        if (exists.ExecuteScalar() is null) return [];

        var allowed = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ParticipantUserName, Direction, SearchStatus, CheckedAtUtc
            FROM InstagramSubscriptionChecks WHERE TargetUserName=$target
            """;
        command.Parameters.AddWithValue("$target", targetUserName);
        using var reader = command.ExecuteReader();
        var results = new List<InstagramSavedCheck>();
        while (reader.Read())
        {
            var userName = reader.GetString(0);
            if (!allowed.Contains(userName)) continue;
            results.Add(new InstagramSavedCheck(userName, reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        }
        return results;
    }
}

public sealed record InstagramSavedCheck(string UserName, string Kind, string Status, string CheckedAtUtc);
