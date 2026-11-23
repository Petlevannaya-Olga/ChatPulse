using System.IO;
using ChatPulse.Desktop.Services;
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
    public async Task DeleteFile_ReleasesPooledConnectionsForBothAccessModes()
    {
        var database = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-{Guid.NewGuid():N}.db");
        try
        {
            await using (var writable = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database
            }.ToString()))
            {
                await writable.OpenAsync();
                using var command = writable.CreateCommand();
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
                using var command = readOnly.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM TestMessages";
                Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            }

            SqliteTestDatabaseCleanup.DeleteFile(database);
            Assert.False(File.Exists(database));
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteFile(database);
        }
    }

    [Fact]
    public async Task DeleteDirectory_ReleasesPoolOpenedWithExplicitReadWriteMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-ReadWrite-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "history.db");

        try
        {
            await using (var initial = new SqliteConnection($"Data Source={database}"))
            {
                await initial.OpenAsync();
                using var create = initial.CreateCommand();
                create.CommandText = "CREATE TABLE TestMessages (Id INTEGER PRIMARY KEY)";
                await create.ExecuteNonQueryAsync();
            }

            // WorkspaceTaskVerificationStore.PublishAsync opens this explicit mode.
            await using (var readWrite = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = database,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString()))
            {
                await readWrite.OpenAsync();
                using var command = readWrite.CreateCommand();
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
    public async Task DeleteFile_ReleasesPoolOpenedWithExplicitReadWriteCreateMode()
    {
        var database = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-ExplicitMode-{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramOwnAccountStore(database);
            await store.SaveAsync("main.account", "other.account");
            Assert.Equal(new InstagramOwnAccounts("main.account", "other.account"), await store.ReadAsync());

            SqliteTestDatabaseCleanup.DeleteFile(database);
            Assert.False(File.Exists(database));
        }
        finally
        {
            if (File.Exists(database)) SqliteTestDatabaseCleanup.DeleteFile(database);
        }
    }

    [Fact]
    public async Task DeleteDirectory_ReleasesPooledConnectionsForEveryDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-Multiple-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            foreach (var databaseName in new[] { "history.db", "chatpulse.db" })
            {
                var database = Path.Combine(directory, databaseName);
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString());
                await connection.OpenAsync();

                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE TestMessages (Id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
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
    public async Task DeleteFile_DoesNotInterruptConnectionToUnrelatedDatabase()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-Scoped-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        var removedDatabase = Path.Combine(testDirectory, "removed.db");
        var activeDatabase = Path.Combine(testDirectory, "active.db");

        try
        {
            await using (var active = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = activeDatabase
            }.ToString()))
            {
                await active.OpenAsync();
                using var createActive = active.CreateCommand();
                createActive.CommandText = "CREATE TABLE Items (Id INTEGER PRIMARY KEY)";
                await createActive.ExecuteNonQueryAsync();

                await using (var removed = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = removedDatabase,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString()))
                {
                    await removed.OpenAsync();
                    using var createRemoved = removed.CreateCommand();
                    createRemoved.CommandText = "CREATE TABLE Items (Id INTEGER PRIMARY KEY)";
                    await createRemoved.ExecuteNonQueryAsync();
                }

                SqliteTestDatabaseCleanup.DeleteFile(removedDatabase);
                Assert.False(File.Exists(removedDatabase));

                // Cleaning one test database must not invalidate an unrelated live connection.
                using var query = active.CreateCommand();
                query.CommandText = "SELECT COUNT(*) FROM Items";
                Assert.Equal(0L, (long)(await query.ExecuteScalarAsync())!);
            }
        }
        finally
        {
            SqliteTestDatabaseCleanup.DeleteDirectory(testDirectory);
        }
    }

    [Fact]
    public async Task DeleteDirectory_ReleasesPoolFromLiteralDataSourceString()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ChatPulse-Cleanup-Literal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "history.db");

        try
        {
            // Match the literal connection strings historically used in integration tests.
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE TestMessages (Id INTEGER PRIMARY KEY)";
                await command.ExecuteNonQueryAsync();
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
