using ChatPulse.Application.Services;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>
/// Ограничивает ожидание запросов библиотеки, в которой нет CancellationToken.
/// По отмене/таймауту вызывает отсоединение старого клиента до освобождения общего gate.
/// Это не гарантирует отмену уже отправленного серверного запроса.
/// </summary>
internal static class TelegramRemoteAwait
{
    internal static async Task<T> AwaitAsync<T>(
        Task<T> request, TimeSpan timeout, CancellationToken cancellationToken,
        Func<Task> abandonClient,
        CancellationToken networkUnavailableToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, networkUnavailableToken);
        try
        {
            return await request.WaitAsync(timeout, linkedCts.Token);
        }
        catch (TimeoutException)
        {
            ObserveLateFailure(request);
            await abandonClient();
            throw new TelegramAuthenticationException("telegram.request_timeout",
                "Не удалось дождаться ответа Telegram. Проверьте интернет-соединение. " +
                "Если код уже пришёл, не запрашивайте его повторно без необходимости.");
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && networkUnavailableToken.IsCancellationRequested)
        {
            ObserveLateFailure(request);
            await abandonClient();
            throw new TelegramAuthenticationException("telegram.network_unavailable",
                "Соединение с интернетом прервано. Проверьте подключение и повторите попытку. " +
                "Если код уже пришёл, не запрашивайте его повторно без необходимости.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateFailure(request);
            await abandonClient();
            throw;
        }
    }

    private static void ObserveLateFailure<T>(Task<T> request)
    {
        // Ошибка запроса, завершившегося уже после таймаута, не должна остаться необработанной.
        _ = request.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
