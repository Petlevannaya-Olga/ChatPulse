using ChatPulse.Application.Features.Chats.Analyze;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Runs a snapshot of chat reviews without depending on WPF controls. The supplied
/// validity predicate prevents in-flight Telegram responses from updating a screen
/// that has been left or replaced by a different Telegram account.
/// </summary>
public sealed class ChatReviewAnalysisCoordinator
{
    public async Task<bool> RunAsync(
        IReadOnlyList<TelegramChatOption> selected,
        IReadOnlyList<ChatAnalysisResultRow> results,
        Func<TelegramChatOption, CancellationToken,
            Task<(ChatActivityAnalysis Analysis, int Days, int Count)>> analyze,
        Func<bool> isCurrent,
        bool retryFailedOnly = false,
        bool includeOtherErrors = false,
        Action<int>? onChatStarted = null,
        Action? onResultChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(analyze);
        ArgumentNullException.ThrowIfNull(isCurrent);

        // A mismatched result array must not attach the analysis of one Telegram
        // peer to a different peer, even if the two chats have identical titles.
        if (selected.Count == 0 || selected.Count != results.Count ||
            selected.Where((chat, index) => !ChatTitlePolicy.IsEligible(chat.Title) ||
                results[index].PeerId != chat.PeerId ||
                !string.Equals(results[index].PeerType, chat.PeerType, StringComparison.Ordinal) ||
                !string.Equals(results[index].Title, chat.Title, StringComparison.Ordinal)).Any())
            return false;

        for (var index = 0; index < selected.Count; index++)
        {
            if (!isCurrent() || cancellationToken.IsCancellationRequested) return false;
            var row = results[index];
            if (retryFailedOnly && !ChatReviewRetryPolicy.ShouldRetry(row, includeOtherErrors))
                continue;

            onChatStarted?.Invoke(index);
            try
            {
                var (analysis, days, count) = await analyze(selected[index], cancellationToken);
                if (!isCurrent() || cancellationToken.IsCancellationRequested) return false;
                row.SetResult(analysis, days, count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (TelegramAuthenticationException exception)
            {
                if (!isCurrent() || cancellationToken.IsCancellationRequested) return false;
                row.SetError(exception.Message, needsConnectionRetry: true);
            }
            catch (Exception)
            {
                if (!isCurrent() || cancellationToken.IsCancellationRequested) return false;
                row.SetError("Не удалось проверить этот чат. Повторите анализ.");
            }
            onResultChanged?.Invoke();
        }
        return isCurrent() && !cancellationToken.IsCancellationRequested;
    }
}
