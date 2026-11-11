using System.IO;
using ChatPulse.Application.Services;
using ChatPulse.Contracts.Telegram;
using ChatPulse.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ChatPulse.Tests;

public sealed class TelegramSetupViewModelTests
{
    private const string HashA = "0123456789abcdef0123456789abcdef";
    private const string HashB = "fedcba9876543210fedcba9876543210";
    private static readonly TelegramLoginProgress Authorized =
        new(TelegramLoginInputKind.None, "Ольга Петлеванная", 1001);

    [Fact]
    public async Task CorruptedApiKeys_ShowDeletionRecovery_WithoutReadingSecrets()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, HashA),
            BeforeIsConfigured = () => throw new InvalidDataException("unreadable keys"),
            BeforeLoad = () => throw new InvalidDataException("unreadable keys")
        };
        var client = new FakeTelegramClient();
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        Assert.True(vm.IsApiCredentialsCorrupted);
        Assert.True(vm.CanDeleteApiCredentials);
        Assert.False(vm.IsApiConfigured);
        Assert.True(await vm.DeleteApiCredentialsAsync());
        Assert.False(vm.IsApiCredentialsCorrupted);
        Assert.Null(store.Value);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task FirstLaunch_NoKeysCannotStartLogin()
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        Assert.False(vm.IsApiConfigured);
        Assert.False(vm.CanStart);
        Assert.False(vm.IsAuthorized);
    }

    [Fact]
    public void ShortPositiveApiId_IsFormatValid_WithoutInlineError()
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        vm.UpdateApiCredentialsDraftValidity("3", HashA);
        Assert.Empty(vm.ApiIdDraftError);
        Assert.True(vm.CanSaveApiCredentials);
    }

    [Theory]
    [InlineData("", "Введите API ID.")]
    [InlineData("0", "API ID должен быть положительным целым числом.")]
    [InlineData("-1", "API ID должен быть положительным целым числом.")]
    [InlineData("2147483648", "API ID должен быть положительным целым числом.")]
    [InlineData("abc", "API ID должен быть положительным целым числом.")]
    public void ApiIdDraftError_UsesExistingValidator(string apiId, string expectedError)
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        vm.UpdateApiCredentialsDraftValidity(apiId, HashA);
        Assert.Equal(expectedError, vm.ApiIdDraftError);
        Assert.Empty(vm.ApiHashDraftError);
        Assert.False(vm.CanSaveApiCredentials);
    }

    [Theory]
    [InlineData("", "Введите API Hash.")]
    [InlineData("0123", "API Hash должен содержать 32 шестнадцатеричных символа.")]
    [InlineData("0123456789abcdef0123456789abcdeg", "API Hash должен содержать 32 шестнадцатеричных символа.")]
    public void ApiHashDraftError_UsesExistingValidator(string apiHash, string expectedError)
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        vm.UpdateApiCredentialsDraftValidity("123", apiHash);
        Assert.Empty(vm.ApiIdDraftError);
        Assert.Equal(expectedError, vm.ApiHashDraftError);
        Assert.False(vm.CanSaveApiCredentials);
    }

    [Fact]
    public void CorrectingApiDraft_ClearsBothInlineErrors()
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        vm.UpdateApiCredentialsDraftValidity("0", "wrong");
        Assert.NotEmpty(vm.ApiIdDraftError);
        Assert.NotEmpty(vm.ApiHashDraftError);
        vm.UpdateApiCredentialsDraftValidity("123", HashA);
        Assert.Empty(vm.ApiIdDraftError);
        Assert.Empty(vm.ApiHashDraftError);
        Assert.True(vm.CanSaveApiCredentials);
    }

    [Theory]
    [InlineData("", "Заполните поле подтверждения.")]
    [InlineData("   ", "Заполните поле подтверждения.")]
    public void EmptyConfirmationDraft_IsRejectedByExistingValidator(string draft, string expectedError)
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();

        vm.UpdateConfirmationDraftValidity(draft);

        Assert.Equal(expectedError, vm.ConfirmationDraftError);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task ConfirmationButton_RequiresNonEmptyDraft_AndUpdatesOnEveryChange()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(
                new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode))
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();

        Assert.True(vm.IsAwaitingInput);
        Assert.False(vm.CanConfirm);

        vm.UpdateConfirmationDraftValidity("12345");
        Assert.Empty(vm.ConfirmationDraftError);
        Assert.True(vm.CanConfirm);

        vm.UpdateConfirmationDraftValidity(" ");
        Assert.Equal("Заполните поле подтверждения.", vm.ConfirmationDraftError);
        Assert.False(vm.CanConfirm);

        vm.UpdateConfirmationDraftValidity("12345");
        Assert.Empty(vm.ConfirmationDraftError);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task ConfirmationDraft_OverMaximumLength_DisablesConfirmAndThenRecovers()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(
                new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode))
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();

        vm.UpdateConfirmationDraftValidity(new string('x', 257));
        Assert.Equal("Значение слишком длинное.", vm.ConfirmationDraftError);
        Assert.False(vm.CanConfirm);

        vm.UpdateConfirmationDraftValidity(new string('x', 256));
        Assert.Empty(vm.ConfirmationDraftError);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task ConfirmationDraftValidity_IsResetBetweenTelegramSteps()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(
                new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode)),
            ContinueImpl = (input, _) => Task.FromResult(input == "12345"
                ? new TelegramLoginProgress(TelegramLoginInputKind.Password)
                : Authorized)
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        vm.UpdateConfirmationDraftValidity("12345");
        Assert.True(vm.CanConfirm);

        await vm.ConfirmAsync("12345");
        Assert.True(vm.IsPasswordRequired);
        Assert.False(vm.CanConfirm);
        Assert.Empty(vm.ConfirmationDraftError);

        vm.UpdateConfirmationDraftValidity("cloud-password");
        Assert.True(vm.CanConfirm);
        await vm.ConfirmAsync("cloud-password");
        Assert.True(vm.IsAuthorized);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public void ConfirmationDraft_DoesNotEnableConfirmWithoutTelegramChallenge()
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();

        vm.UpdateConfirmationDraftValidity("12345");

        Assert.Empty(vm.ConfirmationDraftError);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task CompareKeys_DoesNotExposeStoredHashAndFindsChanges()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        Assert.Equal<bool?>(false, await vm.HaveApiCredentialsChangedAsync("123", HashA.ToUpperInvariant()));
        Assert.Equal<bool?>(true, await vm.HaveApiCredentialsChangedAsync("124", HashA));
        Assert.Equal<bool?>(true, await vm.HaveApiCredentialsChangedAsync("123", HashB));
        Assert.Equal(HashA, store.Value!.ApiHash);
    }

    [Fact]
    public async Task ChangedSavedKeys_RequireConfirmation_WithoutAuthorizedSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient();
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();

        Assert.True(vm.IsApiConfigured);
        Assert.False(vm.IsAuthorized);
        var changed = await vm.HaveApiCredentialsChangedAsync("123", HashB);
        Assert.Equal<bool?>(true, changed);
        Assert.True(vm.ShouldConfirmApiCredentialsChange(changed!.Value));
        // The comparison itself must not save credentials or reset a session.
        Assert.Equal(HashA, store.Value!.ApiHash);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task ChangedSavedKeys_RequireConfirmation_WithAuthorizedSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();

        Assert.True(vm.IsAuthorized);
        var changed = await vm.HaveApiCredentialsChangedAsync("124", HashA);
        Assert.Equal<bool?>(true, changed);
        Assert.True(vm.ShouldConfirmApiCredentialsChange(changed!.Value));
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task UnchangedSavedKeys_DoNotRequireConfirmation()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();

        var changed = await vm.HaveApiCredentialsChangedAsync("123", HashA.ToUpperInvariant());
        Assert.Equal<bool?>(false, changed);
        Assert.False(vm.ShouldConfirmApiCredentialsChange(changed!.Value));
    }

    [Fact]
    public async Task FirstSave_DoesNotRequireChangeConfirmation()
    {
        using var provider = TelegramValidationPipelineTests.BuildProvider(
            new FakeCredentialsStore(), new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();

        var changed = await vm.HaveApiCredentialsChangedAsync("123", HashA);
        Assert.Equal<bool?>(true, changed);
        Assert.False(vm.IsApiConfigured);
        Assert.False(vm.ShouldConfirmApiCredentialsChange(changed!.Value));
    }

    [Fact]
    public async Task CancelCredentialsEdit_DoesNotChangeKeysOrSession()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        vm.BeginEditApiCredentials();
        Assert.True(vm.IsEditingApiCredentials);
        vm.CancelEditApiCredentials();
        Assert.False(vm.IsEditingApiCredentials);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
        Assert.True(vm.IsAuthorized);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task CompareStoreFailure_PreventsConfirmationAndSave()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, HashA),
            BeforeLoad = () => throw new IOException("read error")
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        var result = await vm.HaveApiCredentialsChangedAsync("124", HashB);
        Assert.Null(result);
        Assert.True(vm.HasError);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task SaveFirstKeys_EnablesLoginButDoesNotClaimAuthorized()
    {
        var store = new FakeCredentialsStore();
        var client = new FakeTelegramClient();
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.UpdateApiCredentialsDraftValidity("123", HashA);
        Assert.True(vm.CanSaveApiCredentials);
        Assert.True(await vm.SaveApiCredentialsAsync("123", HashA));
        Assert.True(vm.IsApiConfigured);
        Assert.False(vm.IsAuthorized);
        Assert.Equal(1, client.ResetCalls);
        vm.PhoneNumber = "+79991234567";
        Assert.True(vm.CanStart);
    }

    [Fact]
    public async Task UnchangedKeys_KeepVerifiedAuthorization()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsAuthorized);
        Assert.Equal("ОП", vm.ConnectedAccountInitials);
        vm.BeginEditApiCredentials();
        Assert.True(vm.IsEditingApiCredentials);
        Assert.True(await vm.SaveApiCredentialsAsync("123", HashA.ToUpperInvariant()));
        Assert.True(vm.IsAuthorized);
        Assert.False(vm.IsEditingApiCredentials);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task ChangedKeys_ClearAuthorization()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(await vm.SaveApiCredentialsAsync("123", HashB));
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsApiConfigured);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task SaveWriteError_DoesNotInvalidateVerifiedSession()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, HashA),
            BeforeSave = (_, _) => throw new IOException("disk failure")
        };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.False(await vm.SaveApiCredentialsAsync("124", HashB));
        Assert.True(vm.IsAuthorized);
        Assert.Equal(0, client.ResetCalls);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task ResetFailure_InvalidatesOldAuthorizedBadge()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => Task.FromResult(Authorized),
            ResetImpl = _ => throw new IOException("session write failure")
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsAuthorized);
        Assert.False(await vm.ResetTelegramSessionAsync());
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsApiConfigured);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task ResetSuccess_PreservesApiCredentials()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(await vm.ResetTelegramSessionAsync());
        Assert.True(vm.IsApiConfigured);
        Assert.False(vm.IsAuthorized);
        Assert.Equal(new TelegramApiCredentials(123, HashA), store.Value);
    }

    [Fact]
    public async Task DeleteKeys_ClearsBothApiStatusAndLogin()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(await vm.DeleteApiCredentialsAsync());
        Assert.False(vm.IsApiConfigured);
        Assert.False(vm.IsAuthorized);
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task DeleteKeys_WhenFileDeleteFails_KeepsAuthorizedSession()
    {
        var store = new FakeCredentialsStore
        {
            Value = new TelegramApiCredentials(123, HashA),
            BeforeDelete = _ => throw new IOException("permission denied")
        };
        var client = new FakeTelegramClient { RestoreImpl = _ => Task.FromResult(Authorized) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.False(await vm.DeleteApiCredentialsAsync());
        Assert.True(vm.IsAuthorized);
        Assert.Equal(0, client.ResetCalls);
    }

    [Fact]
    public async Task RestoreFromSavedKeyButNoSession_DoesNotShowConnected()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.True(vm.IsApiConfigured);
        Assert.False(vm.IsAuthorized);
    }

    [Theory]
    [InlineData("telegram.session_unavailable")]
    [InlineData("telegram.session_revoked")]
    public async Task UnusableSavedSession_ShowsExplicitRecoveryAction(string errorCode)
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => throw new TelegramAuthenticationException(errorCode, "Session unavailable")
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.IsSessionRecoveryNeeded);
        Assert.True(vm.CanResetTelegramSession);
        Assert.True(await vm.ResetTelegramSessionAsync());
        Assert.False(vm.IsSessionRecoveryNeeded);
        Assert.Equal(1, client.ResetCalls);
    }

    [Fact]
    public async Task RestoreFailure_ReportsSafeError()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            RestoreImpl = _ => throw new TelegramAuthenticationException("telegram.session_restore_failed", "Test restore failed")
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        await vm.RestoreSessionAsync();
        Assert.False(vm.IsAuthorized);
        Assert.True(vm.HasError);
        Assert.False(vm.IsSessionRecoveryNeeded); // network errors must not encourage deletion
    }

    [Theory]
    [InlineData("+79991234567", true)]
    [InlineData("+7999123456", false)]
    [InlineData("+7abcdefghij", false)]
    [InlineData("+7１２３４５６７８９０", false)]
    public async Task PhoneCompleteness_MatchesValidator(string phone, bool expected)
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, new FakeTelegramClient());
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = phone;
        Assert.Equal(expected, vm.IsPhoneComplete);
    }

    [Fact]
    public async Task BeginLogin_ThenCloudPassword_ThenAuthorized()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(new TelegramLoginProgress(TelegramLoginInputKind.VerificationCode)),
            ContinueImpl = (input, _) => Task.FromResult(input == "code"
                ? new TelegramLoginProgress(TelegramLoginInputKind.Password)
                : Authorized)
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.IsAwaitingInput);
        Assert.False(vm.IsAuthorized);
        await vm.ConfirmAsync("code");
        Assert.True(vm.IsPasswordRequired);
        await vm.ConfirmAsync("password");
        Assert.True(vm.IsAuthorized);
        Assert.Equal("Ольга Петлеванная", vm.ConnectedAccount);
    }

    [Fact]
    public async Task BeginLogin_InvalidPhoneRejectedByDecorator()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient();
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+7abcdefghij";
        await vm.BeginAsync();
        Assert.True(vm.HasError);
        Assert.Equal(0, client.BeginCalls);
    }

    [Fact]
    public async Task ResendOnlyEnabledAfterTelegramDeadline()
    {
        var store = new FakeCredentialsStore { Value = new TelegramApiCredentials(123, HashA) };
        var client = new FakeTelegramClient
        {
            BeginImpl = (_, _) => Task.FromResult(new TelegramLoginProgress(
                TelegramLoginInputKind.VerificationCode,
                HasResendOption: true,
                ResendAvailableAtUtc: DateTimeOffset.UtcNow.AddMinutes(2)))
        };
        using var provider = TelegramValidationPipelineTests.BuildProvider(store, client);
        var vm = provider.GetRequiredService<TelegramSetupViewModel>();
        await vm.LoadApiCredentialsStatusAsync();
        vm.PhoneNumber = "+79991234567";
        await vm.BeginAsync();
        Assert.True(vm.HasResendOption);
        Assert.False(vm.CanResendCode);
        await vm.ResendCodeAsync();
        Assert.Equal(0, client.ResendCalls);
    }
}
