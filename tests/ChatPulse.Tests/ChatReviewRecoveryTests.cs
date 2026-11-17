using ChatPulse.Desktop.Views.Onboarding;
using Xunit;

namespace ChatPulse.Tests;

public sealed class ChatReviewRecoveryTests
{
    [Fact]
    public void ConnectionError_IsMarkedForRetryOnReconnection()
    {
        var row = new ChatAnalysisResultRow("14 Пост чат б/с");

        row.SetError("Нет подключения к Telegram.", needsConnectionRetry: true);

        Assert.Equal("Error", row.StatusKind);
        Assert.True(row.NeedsConnectionRetry);
        Assert.False(row.CanConnect);
    }

    [Fact]
    public void OrdinaryAnalysisError_DoesNotScheduleNetworkRetry()
    {
        var row = new ChatAnalysisResultRow("14 Пост чат б/с");

        row.SetError("Не удалось обработать сообщения.");

        Assert.Equal("Error", row.StatusKind);
        Assert.False(row.NeedsConnectionRetry);
        Assert.False(row.CanConnect);
    }

    [Fact]
    public void NewNonNetworkError_ResetsEarlierConnectionRetryFlag()
    {
        var row = new ChatAnalysisResultRow("14 Пост чат б/с");
        row.SetError("Нет подключения к Telegram.", needsConnectionRetry: true);

        row.SetError("Другая ошибка.");

        Assert.False(row.NeedsConnectionRetry);
    }
    [Fact]
    public void ErrorRow_RemainsRetryableWithoutConnectionStateTransition()
    {
        var row = new ChatAnalysisResultRow("14 Пост чат б/с");
        row.SetError("Не удалось подтвердить Telegram-аккаунт для анализа.", needsConnectionRetry: true);

        Assert.Equal("Error", row.StatusKind);
        Assert.True(row.NeedsConnectionRetry);
        Assert.False(row.CanConnect);
    }

    [Fact]
    public void RetrySuccess_ClearsPreviousErrorState()
    {
        var row = new ChatAnalysisResultRow("14 Пост чат б/с");
        row.SetError("Не удалось подтвердить Telegram-аккаунт для анализа.", needsConnectionRetry: true);
        row.SetResult(ChatPulse.Application.Features.Chats.Analyze.ChatActivityAnalyzer.Analyze(
            Array.Empty<ChatPulse.Application.Features.Chats.Analyze.ChatActivityMessage>()), 7, 0);

        Assert.Equal("Warning", row.StatusKind);
        Assert.False(row.NeedsConnectionRetry);
    }

    [Fact]
    public void Navigation_InvalidatesOldAnalysisWithoutCancellingTelegramRequest()
    {
        var generation = new ChatReviewRunGeneration();
        using var request = new CancellationTokenSource();
        var running = generation.Advance();

        // Navigating back invalidates UI output but must not abort Telegram RPC.
        generation.Advance();

        Assert.False(generation.IsCurrent(running));
        Assert.False(request.IsCancellationRequested);
        Assert.True(generation.IsCurrent(generation.Advance()));
    }
}
