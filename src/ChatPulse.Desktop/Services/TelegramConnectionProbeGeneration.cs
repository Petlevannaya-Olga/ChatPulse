namespace ChatPulse.Desktop.Services;

/// <summary>
/// Invalidates completion of an old connection probe after disconnect, session reset
/// or a newer probe. It does not cancel active Telegram RPCs by itself.
/// </summary>
public sealed class TelegramConnectionProbeGeneration
{
    private long _current;

    public long Advance() => Interlocked.Increment(ref _current);

    public bool IsCurrent(long generation) => generation == Interlocked.Read(ref _current);
}
