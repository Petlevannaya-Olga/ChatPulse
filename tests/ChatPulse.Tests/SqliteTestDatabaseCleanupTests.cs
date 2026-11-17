using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class SqliteTestDatabaseCleanupTests
{
    [Fact]
    public async Task DeleteDirectory_ReleasesPooledConnectionsForBothAccessModes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "history.db");

        try
        {
            await using (var writable = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database
            }.ToString()))
            {
                await writable.OpenAsync();
                var command = writable.CreateCommand();
                command.CommandText = "CREATE TABLE TestMessages (Id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
            }

            await using (var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString()))
            {
                await readOnly.OpenAsync();
                var command = readOnly.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM TestMessages";
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            }

            SqliteTestDatabaseCleanup.DeleteDirectory(directory);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
                SqliteTestDatabaseCleanup.DeleteDirectory(directory);
        }
    }

    [Fact]
    public void DeleteDirectory_WithoutDatabase_RemovesDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SqliteTestDatabaseCleanup.DeleteDirectory(directory);
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
