using System.IO;
using ChatPulse.Desktop.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ChatPulse.Tests;

public sealed class InstagramOwnAccountTests
{
    [Fact]
    public async Task OwnProfilesPersistInSqliteAndBackupCanBeCleared()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChatPulseOwnAccounts_{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramOwnAccountStore(path);
            await store.SaveAsync("@Main.Profile", "@Other.Active");
            Assert.Equal(new InstagramOwnAccounts("main.profile", "other.active"),
                await new InstagramOwnAccountStore(path).ReadAsync());

            await store.SaveAsync("main.profile", null);
            Assert.Equal(new InstagramOwnAccounts("main.profile", null), await store.ReadAsync());

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM InstagramOwnAccounts";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task LegacyReceivingProfileIsImportedWithoutLosingBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChatPulseOwnAccounts_{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramOwnAccountStore(path);
            Assert.Equal(new InstagramOwnAccounts("main.profile", null),
                await store.EnsureFromLegacyAsync("@main.profile"));
            await store.SaveAsync("main.profile", "backup.active");
            Assert.Equal(new InstagramOwnAccounts("new.main", "backup.active"),
                await store.EnsureFromLegacyAsync("new.main"));
            Assert.Equal(new InstagramOwnAccounts("new.main", "backup.active"),
                await new InstagramOwnAccountStore(path).ReadAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task InvalidBackupDoesNotOverwriteSavedAccounts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ChatPulseOwnAccounts_{Guid.NewGuid():N}.db");
        try
        {
            var store = new InstagramOwnAccountStore(path);
            await store.SaveAsync("main", "backup");
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync("main", "main"));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveAsync("main", "wrong/url"));
            Assert.Equal(new InstagramOwnAccounts("main", "backup"), await store.ReadAsync());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void OwnProfilesAreNotOfferedForSubscriptionChecks()
    {
        var owned = InstagramOwnAccountFilter.GetUserNames(
            new InstagramOwnAccounts("main.profile", "backup.active"), "viewer.user");
        var participants = new[]
        {
            new InstagramParticipantOption(1, "Основной", "", "MAIN.PROFILE", "Обычный"),
            new InstagramParticipantOption(2, "Резервный", "", "backup.active", "Обычный"),
            new InstagramParticipantOption(3, "Проверочный", "", "viewer.user", "Обычный"),
            new InstagramParticipantOption(4, "Моя ссылка подписки", "", "other.activity", "Обычный",
                "https://www.instagram.com/main.profile/"),
            new InstagramParticipantOption(5, "Участница", "https://www.instagram.com/member.active/",
                "member.active", "Обычный", "https://www.instagram.com/member.subscribe/"),
            new InstagramParticipantOption(6, "VIP", "", "vip.user", "VIP",
                "https://www.instagram.com/vip.subscribe/"),
        };

        Assert.True(InstagramOwnAccountFilter.IsOwnParticipant(participants[3], owned));
        Assert.False(InstagramOwnAccountFilter.IsOwnParticipant(participants[4], owned));
        var selection = InstagramBatchEligibility.Prepare(participants, owned);
        Assert.Equal(new[] { "member.active" }, selection.FollowsMeUserNames);
        Assert.Equal(new[] { "member.subscribe", "vip.subscribe" }, selection.IFollowUserNames);
        Assert.Equal(4, selection.OwnAccountsExcludedCount);
        Assert.Equal(1, selection.ExcludedCount);
    }
}
