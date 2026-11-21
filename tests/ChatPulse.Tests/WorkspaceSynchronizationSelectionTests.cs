using ChatPulse.Desktop.Services;
using ChatPulse.Desktop.Settings;
using Xunit;

namespace ChatPulse.Tests;

public sealed class WorkspaceSynchronizationSelectionTests
{
    private static ConfiguredChat Chat(long id, ChatSynchronizationState state = ChatSynchronizationState.COMPLETED)
        => new()
        {
            TelegramUserId = 101,
            PeerId = id,
            PeerType = "Channel",
            SynchronizationState = state
        };

    [Fact]
    public void AllChatsSelectsOnlyCompleted()
    {
        var first = Chat(1);
        var second = Chat(2);
        var pending = Chat(3, ChatSynchronizationState.PENDING);
        Assert.Equal(new[] { first, second },
            WorkspaceSynchronizationSelection.Select([first, pending, second]));
    }

    [Fact]
    public void IndividualSelectionNeverIncludesOtherChats()
    {
        var first = Chat(1);
        var second = Chat(2);
        var third = Chat(3);
        var selected = WorkspaceSynchronizationSelection.Select([first, second, third], Chat(2));
        Assert.Equal(second, Assert.Single(selected));
    }

    [Fact]
    public void SamePeerOwnedByAnotherTelegramAccountIsNotSelected()
    {
        var own = Chat(7);
        var differentAccount = Chat(7);
        differentAccount.TelegramUserId = 202;
        Assert.Equal(own, Assert.Single(WorkspaceSynchronizationSelection.Select(
            [own, differentAccount], Chat(7))));
    }

    [Fact]
    public void PendingSelectedChatIsNotSynchronized()
    {
        var pending = Chat(7, ChatSynchronizationState.RUNNING);
        Assert.Empty(WorkspaceSynchronizationSelection.Select([pending], pending));
    }
}
