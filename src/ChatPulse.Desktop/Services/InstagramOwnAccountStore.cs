using ChatPulse.Desktop.Settings;
using Microsoft.Data.Sqlite;

namespace ChatPulse.Desktop.Services;

/// <summary>
/// The owner's Instagram identities, not the credentials of the Selenium browser.
/// The receiving profile is also the target of the existing subscription checks.
/// </summary>
public sealed record InstagramOwnAccounts(string ReceivingUserName, string? GivingUserName);

public static class InstagramOwnAccountFilter
{
    public static HashSet<string> GetUserNames(InstagramOwnAccounts? accounts, string? seleniumUserName = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (accounts is not null)
        {
            names.Add(accounts.ReceivingUserName);
            if (!string.IsNullOrWhiteSpace(accounts.GivingUserName))
                names.Add(accounts.GivingUserName);
        }
        if (!string.IsNullOrWhiteSpace(seleniumUserName))
            names.Add(seleniumUserName);
        return names;
    }

    public static bool IsOwnParticipant(InstagramParticipantOption participant, IReadOnlySet<string> ownUserNames)
    {
        if (!string.IsNullOrWhiteSpace(participant.UserName) && ownUserNames.Contains(participant.UserName))
            return true;
        var subscriptionUserName = InstagramActivityProfile.TryGetUserName(participant.SubscriptionProfileUrl);
        return subscriptionUserName is not null && ownUserNames.Contains(subscriptionUserName);
    }
}

/// <summary>
/// SQLite source of truth for the owner's profiles. Existing settings.json values are
/// imported for compatibility, but the optional giving account is stored only here.
/// </summary>
public sealed class InstagramOwnAccountStore(string databasePath)
{
    private const string RECEIVING = "RECEIVING";
    private const string GIVING = "GIVING";

    public async Task<InstagramOwnAccounts?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT AccountKind, UserName FROM InstagramOwnAccounts";
        string? receiving = null;
        string? giving = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(0) == RECEIVING) receiving = reader.GetString(1);
            else if (reader.GetString(0) == GIVING) giving = reader.GetString(1);
        }
        return receiving is null ? null : new InstagramOwnAccounts(receiving, giving);
    }

    public async Task<InstagramOwnAccounts?> EnsureFromLegacyAsync(string? legacyReceiving,
        CancellationToken cancellationToken = default)
    {
        var accounts = await ReadAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(legacyReceiving)) return accounts;
        var normalized = InstagramUserName.Normalize(legacyReceiving);
        if (accounts is not null && string.Equals(accounts.ReceivingUserName,
                normalized, StringComparison.OrdinalIgnoreCase)) return accounts;
        await SaveAsync(normalized, accounts?.GivingUserName, cancellationToken);
        return new InstagramOwnAccounts(normalized, accounts?.GivingUserName);
    }

    public async Task SaveAsync(string receiving, string? giving,
        CancellationToken cancellationToken = default)
    {
        var receivingName = InstagramUserName.Normalize(receiving);
        var givingName = string.IsNullOrWhiteSpace(giving) ? null : InstagramUserName.Normalize(giving);
        if (string.Equals(receivingName, givingName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Резервный аккаунт должен отличаться от основного.", nameof(giving));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var remove = connection.CreateCommand();
        remove.Transaction = (SqliteTransaction)transaction;
        remove.CommandText = "DELETE FROM InstagramOwnAccounts";
        await remove.ExecuteNonQueryAsync(cancellationToken);
        foreach (var (kind, name) in new[] { (RECEIVING, receivingName), (GIVING, givingName) })
        {
            if (name is null) continue;
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO InstagramOwnAccounts (AccountKind, UserName, UpdatedAtUtc)
                VALUES ($kind, $name, $changed)
                """;
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$changed", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        try
        {
            var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS InstagramOwnAccounts (
                    AccountKind TEXT PRIMARY KEY CHECK (AccountKind IN ('RECEIVING','GIVING')),
                    UserName TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    UpdatedAtUtc TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
