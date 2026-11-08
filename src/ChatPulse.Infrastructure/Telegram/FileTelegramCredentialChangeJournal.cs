using ChatPulse.Application.Services;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>Crash-recovery marker stored next to encrypted credentials/session.</summary>
public sealed class FileTelegramCredentialChangeJournal(string dataDirectory)
    : ITelegramCredentialChangeJournal
{
    private readonly string _path = Path.Combine(dataDirectory, "telegram.credentials-update.pending");

    public async Task MarkPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync("pending"u8.ToArray(), cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public Task<bool> IsPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_path));
    }

    public Task ClearPendingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(_path);
        return Task.CompletedTask;
    }
}
