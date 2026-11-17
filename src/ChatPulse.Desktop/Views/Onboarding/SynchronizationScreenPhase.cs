namespace ChatPulse.Desktop.Views.Onboarding;

/// <summary>Visual phase of the synchronization step, not a completion flag for onboarding.</summary>
public enum SynchronizationScreenPhase
{
    CONFIGURATION,
    SYNCHRONIZING,
    HISTORY_RESULT,
}

public static class SynchronizationScreenPhaseResolver
{
    public static SynchronizationScreenPhase Resolve(int totalChats, int importedChats, bool isBusy)
    {
        if (totalChats < 0 || importedChats < 0 || importedChats > totalChats)
            throw new ArgumentOutOfRangeException(nameof(importedChats));
        if (isBusy) return SynchronizationScreenPhase.SYNCHRONIZING;
        return totalChats > 0 && importedChats == totalChats
            ? SynchronizationScreenPhase.HISTORY_RESULT
            : SynchronizationScreenPhase.CONFIGURATION;
    }
}
