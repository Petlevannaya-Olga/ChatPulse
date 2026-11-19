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
        if (File.Exists(database)) ClearDatabasePools(database);

        // An IOException here indicates an undisposed live connection: do not mask it.
        Directory.Delete(directory, recursive: true);
    }

    public static void DeleteFile(string databasePath)
    {
        if (!File.Exists(databasePath)) return;

        ClearDatabasePools(databasePath);
        // Do not suppress IOException: it indicates a still-open connection or another file owner.
        File.Delete(databasePath);
    }

    private static void ClearDatabasePools(string databasePath)
    {
        ClearPool(new SqliteConnectionStringBuilder { DataSource = databasePath });
        // InstagramOwnAccountStore sets Mode explicitly, creating a separate pool
        // from connections that rely on the default ReadWriteCreate mode.
        ClearPool(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        });
        ClearPool(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        });
    }

    private static void ClearPool(SqliteConnectionStringBuilder builder)
    {
        using var connection = new SqliteConnection(builder.ToString());
        SqliteConnection.ClearPool(connection);
    }
}
