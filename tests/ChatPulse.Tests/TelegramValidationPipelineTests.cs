using ChatPulse.Application;
using ChatPulse.Application.Features.Telegram.ApiCredentials.Save;
using ChatPulse.Application.Features.Telegram.BeginLogin;
using ChatPulse.Application.Services;
using ChatPulse.Shared.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramValidationPipelineTests
{
    [Fact]
    public async Task InvalidApiCredentials_StoppedByDecorator_BeforeParsingOrSaving()
    {
        var store = new FakeCredentialsStore();
        var client = new FakeTelegramClient();
        using var provider = BuildProvider(store, client);
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            ICommandHandler<bool, SaveTelegramApiCredentialsCommand>>();

        var result = await handler.Handle(new SaveTelegramApiCredentialsCommand("abc", "invalid"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, client.ResetCalls);
        Assert.NotNull(result.Error.Errors);
        Assert.Contains(result.Error.Errors!, issue => issue.Field == "api_id");
        Assert.Contains(result.Error.Errors!, issue => issue.Field == "api_hash");
    }

    [Fact]
    public async Task ValidApiCredentials_AreForwardedToRealHandler()
    {
        var store = new FakeCredentialsStore();
        var client = new FakeTelegramClient();
        using var provider = BuildProvider(store, client);
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            ICommandHandler<bool, SaveTelegramApiCredentialsCommand>>();

        var result = await handler.Handle(
            new SaveTelegramApiCredentialsCommand("123", "0123456789abcdef0123456789abcdef"), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(123, store.Value!.ApiId);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task InvalidPhone_IsRejectedBeforeTelegramRequest()
    {
        var store = new FakeCredentialsStore();
        var client = new FakeTelegramClient();
        using var provider = BuildProvider(store, client);
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            ICommandHandler<ChatPulse.Contracts.Telegram.TelegramLoginProgress, BeginTelegramLoginCommand>>();

        var result = await handler.Handle(new BeginTelegramLoginCommand("+7900abc4567"), default);

        Assert.True(result.IsFailure);
        Assert.Equal(0, client.BeginCalls);
    }

    internal static ServiceProvider BuildProvider(FakeCredentialsStore store, FakeTelegramClient client)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<ITelegramApiCredentialsStore>(store);
        services.AddSingleton<ITelegramAuthenticationClient>(client);
        services.AddSingleton<ITelegramCredentialChangeJournal>(new FakeCredentialChangeJournal());
        services.AddSingleton<TelegramAuthorizationEvents>();
        services.AddSingleton<ChatPulse.Desktop.ViewModels.TelegramSetupViewModel>();
        return services.BuildServiceProvider();
    }
}
