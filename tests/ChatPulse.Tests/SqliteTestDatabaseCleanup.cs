using System.IO;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Tests;

/// <summary>
/// Releases pooled handles for the test's own SQLite files before deleting them.
/// Never clear all pools here: xUnit runs other tests with live connections in parallel.
/// Every connection/reader/command belonging to the deleted database must already be disposed.
/// </summary>
internal static class SqliteTestDatabaseCleanup
{
    public static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;

        // A test directory may contain both history.db and chatpulse.db.
        // Clear their pools, not the pools of unrelated tests.
        foreach (var databasePath in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
            ClearDatabasePools(databasePath);

        // Do not hide IOExceptions caused by live connections or external file owners.
        Directory.Delete(directory, recursive: true);
    }

    public static void DeleteFile(string databasePath)
    {
        if (!File.Exists(databasePath)) return;

        ClearDatabasePools(databasePath);
        File.Delete(databasePath);
    }

    private static void ClearDatabasePools(string databasePath)
    {
        // The application and tests use both the implicit default mode and explicit
        // modes. These are distinct connection strings and may have separate pools.
        ClearPool(new SqliteConnectionStringBuilder { DataSource = databasePath });
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

    private static void ClearPool(SqliteConnectionStringBuilder connectionString)
    {
        using var connection = new SqliteConnection(connectionString.ToString());
        SqliteConnection.ClearPool(connection);
    }
}
