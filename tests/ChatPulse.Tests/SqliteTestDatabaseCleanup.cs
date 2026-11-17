using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Tests;

/// <summary>
/// Deletes an isolated SQLite test database after releasing the connection pools
/// used by ChatSynchronizationService, test assertions and WorkspaceTaskReader.
/// This helper must only be called after the test has disposed all open connections.
/// </summary>
internal static class SqliteTestDatabaseCleanup
{
    public static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        var database = Path.Combine(directory, "history.db");
        if (File.Exists(database))
        {
            ClearPool(new SqliteConnectionStringBuilder { DataSource = database });
            ClearPool(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly
            });
        }

        // An IOException here indicates an undisposed live connection: do not mask it.
        Directory.Delete(directory, recursive: true);
    }

    private static void ClearPool(SqliteConnectionStringBuilder builder)
    {
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }
}
