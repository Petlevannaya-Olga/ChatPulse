using System.Reflection;
using System.Runtime.CompilerServices;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramConnectionMonitorTests
{
    [Theory]
    [InlineData(TelegramConnectionStatus.OFFLINE, true)]
    [InlineData(TelegramConnectionStatus.CHECKING, true)]
    [InlineData(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE, true)]
    [InlineData(TelegramConnectionStatus.ONLINE, false)]
    public void BannerVisibility_DoesNotRequireAuthorizedTelegramSession(
        TelegramConnectionStatus status, bool expectedVisible)
    {
        // Avoid opening a WPF window and running real network probes: only
        // exercise the computed notification state with _active == false.
        var monitor = (TelegramConnectionMonitor)
            RuntimeHelpers.GetUninitializedObject(typeof(TelegramConnectionMonitor));
        var statusField = typeof(TelegramConnectionMonitor).GetField(
            "_status", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(statusField);
        statusField.SetValue(monitor, status);

        Assert.Equal(expectedVisible, monitor.IsWarningVisible);
    }
}
