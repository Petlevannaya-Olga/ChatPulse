using System.Reflection;
using System.Runtime.CompilerServices;
using ChatPulse.Desktop.Services;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramConnectionLifecycleTests
{
    [Fact]
    public void Disconnection_InvalidatesInFlightConnectionProbe()
    {
        var generation = new TelegramConnectionProbeGeneration();
        var inFlight = generation.Advance();
        Assert.True(generation.IsCurrent(inFlight));

        generation.Advance(); // connection lost / monitoring stopped

        Assert.False(generation.IsCurrent(inFlight));
    }

    [Fact]
    public void NewProbe_DiscardsOldTelegramReplyAfterReconnection()
    {
        var generation = new TelegramConnectionProbeGeneration();
        var oldProbe = generation.Advance();
        generation.Advance(); // offline
        var newProbe = generation.Advance(); // online again

        Assert.False(generation.IsCurrent(oldProbe));
        Assert.True(generation.IsCurrent(newProbe));
    }

    [Fact]
    public async Task ConcurrentProbes_ReceiveDistinctGenerations()
    {
        var generation = new TelegramConnectionProbeGeneration();
        var runs = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(generation.Advance)));

        Assert.Equal(100, runs.Distinct().Count());
        Assert.Single(runs.Where(generation.IsCurrent));
    }

    [Theory]
    [InlineData(TelegramConnectionStatus.ONLINE, true, true, false, true, false, false)]
    [InlineData(TelegramConnectionStatus.OFFLINE, true, false, false, false, true, false)]
    [InlineData(TelegramConnectionStatus.CHECKING, true, true, true, false, true, false)]
    [InlineData(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE, true, true, false, false, true, true)]
    [InlineData(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE, false, true, false, true, true, false)]
    [InlineData(TelegramConnectionStatus.TELEGRAM_UNAVAILABLE, true, false, false, false, true, false)]
    public void MonitorUiState_ReflectsConnectionAndRetryAvailability(
        TelegramConnectionStatus status, bool active, bool networkAvailable, bool checking,
        bool canUseTelegram, bool warning, bool canRetry)
    {
        // No real network, DispatcherTimer or Telegram RPC is started by this test.
        var monitor = (TelegramConnectionMonitor)
            RuntimeHelpers.GetUninitializedObject(typeof(TelegramConnectionMonitor));
        SetField(monitor, "_status", status);
        SetField(monitor, "_active", active);
        SetField(monitor, "_networkAvailable", networkAvailable);
        SetField(monitor, "_checking", checking);

        Assert.Equal(canUseTelegram, monitor.CanUseTelegram);
        Assert.Equal(warning, monitor.IsWarningVisible);
        Assert.Equal(canRetry, monitor.CanRetry);
        Assert.Equal(status == TelegramConnectionStatus.ONLINE, string.IsNullOrEmpty(monitor.NoticeText));
    }

    private static void SetField(TelegramConnectionMonitor monitor, string name, object value)
    {
        var field = typeof(TelegramConnectionMonitor).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(monitor, value);
    }
}
