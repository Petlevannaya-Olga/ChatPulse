using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// The previous version checked both directions using ActivityProfileUrl. Its "following"
/// results and summaries cannot be displayed as results for SubscriptionProfileUrl.
/// Apply this one-time migration before reading or writing Instagram check results.
/// </summary>
public sealed class InstagramCheckProfileSemanticsMigration(string databasePath)
{
    public void EnsureApplied()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            CREATE TABLE IF NOT EXISTS InstagramCheckProfileSemantics (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL
            )
            """;
        check.ExecuteNonQuery();
        check.CommandText = "SELECT Version FROM InstagramCheckProfileSemantics WHERE Id=1";
        if (check.ExecuteScalar() is long version && version >= 2)
        {
            transaction.Commit();
            return;
        }

        // Keep the valid old "followers" checks; invalidate following checks and summaries
        // created with the wrong target account. Never infer correctness from the URL text.
        foreach (var sql in new[]
                 {
                     "DELETE FROM InstagramSubscriptionChecks WHERE Direction='following'",
                     "DELETE FROM InstagramBatchSummaries",
                     "DELETE FROM InstagramBatchCheckDrafts",
                     "DROP TABLE IF EXISTS InstagramBatchInterruptedRuns"
                 })
        {
            var table = sql.Contains("InstagramSubscriptionChecks", StringComparison.Ordinal) ?
                "InstagramSubscriptionChecks" :
                sql.Contains("InstagramBatchSummaries", StringComparison.Ordinal) ? "InstagramBatchSummaries" :
                sql.Contains("InstagramBatchCheckDrafts", StringComparison.Ordinal) ? "InstagramBatchCheckDrafts" :
                "InstagramBatchInterruptedRuns";
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
            exists.Parameters.AddWithValue("$name", table);
            if (exists.ExecuteScalar() is null) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = """
            INSERT INTO InstagramCheckProfileSemantics (Id, Version) VALUES (1,2)
            ON CONFLICT(Id) DO UPDATE SET Version=excluded.Version
            """;
        mark.ExecuteNonQuery();
        transaction.Commit();
    }
}
