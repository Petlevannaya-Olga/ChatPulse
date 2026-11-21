using System.IO;
using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceSynchronizationStampTests
{
    private static ConfiguredChat Chat(long peerId) => new()
    {
        TelegramUserId = 91,
        PeerId = peerId,
        PeerType = "Channel",
        TelegramTitle = $"Чат {peerId}",
        SocialNetwork = "Instagram"
    };

    [Fact]
    public async Task IndividualSyncStampIsIsolatedAndSurvivesReopening()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-sync-stamps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chatpulse.db");
        try
        {
            var first = Chat(14);
            var second = Chat(15);
            var date = new DateTimeOffset(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);
            var store = new WorkspaceSynchronizationStampStore(path);
            await store.RecordChatSuccessAsync(first, date);

            var reopened = new WorkspaceSynchronizationStampStore(path);
            Assert.Equal(date, await reopened.ReadChatAsync(first));
            Assert.Null(await reopened.ReadChatAsync(second));
            Assert.Null(await reopened.ReadAllAsync(91));
        }
        finally { SqliteTestDatabaseCleanup.DeleteDirectory(directory); }
    }

    [Fact]
    public async Task GlobalSyncStampDoesNotModifyIndividualStamps()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-sync-global-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chatpulse.db");
        try
        {
            var store = new WorkspaceSynchronizationStampStore(path);
            var first = Chat(14);
            var t1 = DateTimeOffset.UtcNow.AddMinutes(-3);
            var t2 = DateTimeOffset.UtcNow;
            await store.RecordChatSuccessAsync(first, t1);
            await store.RecordAllSuccessAsync(91, t2);
            Assert.Equal(t1, await store.ReadChatAsync(first));
            Assert.Equal(t2, await store.ReadAllAsync(91));
            Assert.Null(await store.ReadAllAsync(92));
        }
        finally { SqliteTestDatabaseCleanup.DeleteDirectory(directory); }
    }

    [Fact]
    public async Task GlobalSuccessPersistsMessageCountWithoutChangingChatStamps()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-sync-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chatpulse.db");
        try
        {
            var store = new WorkspaceSynchronizationStampStore(path);
            var chat = Chat(14);
            var oldChatStamp = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
            var completed = new DateTimeOffset(2026, 11, 21, 14, 47, 0, TimeSpan.Zero);
            await store.RecordChatSuccessAsync(chat, oldChatStamp);
            await store.RecordAllSuccessAsync(91, completed, 0);
            var reopened = new WorkspaceSynchronizationStampStore(path);
            var summary = await reopened.ReadAllSummaryAsync(91);
            Assert.Equal(completed, summary.CompletedAt);
            Assert.Equal(0, summary.NewMessages);
            Assert.Equal(oldChatStamp, await reopened.ReadChatAsync(chat));
            Assert.Null((await reopened.ReadAllSummaryAsync(92)).CompletedAt);
        }
        finally { SqliteTestDatabaseCleanup.DeleteDirectory(directory); }
    }

    [Fact]
    public async Task OldGlobalStampHasUnknownCountUntilNextCompletedSync()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"chatpulse-sync-old-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chatpulse.db");
        try
        {
            var store = new WorkspaceSynchronizationStampStore(path);
            var previous = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
            await store.RecordAllSuccessAsync(91, previous);
            Assert.Null((await store.ReadAllSummaryAsync(91)).NewMessages);
            var next = previous.AddDays(1);
            await store.RecordAllSuccessAsync(91, next, 15);
            var summary = await store.ReadAllSummaryAsync(91);
            Assert.Equal(next, summary.CompletedAt);
            Assert.Equal(15, summary.NewMessages);
        }
        finally { SqliteTestDatabaseCleanup.DeleteDirectory(directory); }
    }
}
