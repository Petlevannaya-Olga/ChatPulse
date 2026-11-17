using System.Threading;

namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>
/// Rejects UI results of asynchronous screen loads after navigation, owner changes,
/// or a newer request. Does not cancel an in-flight Telegram RPC.
/// </summary>
public sealed class OnboardingRequestGeneration
{
    private long _current;

    public long Advance() => Interlocked.Increment(ref _current);

    public bool IsCurrent(long generation) =>
        generation > 0 && generation == Interlocked.Read(ref _current);
}
