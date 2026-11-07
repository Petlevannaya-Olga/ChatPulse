using System.Security.Cryptography;
using System.Text.Json;
using ChatPulse.Application.Services;

namespace ChatPulse.Infrastructure.Telegram;

/// <summary>Локальное защищённое хранилище личных параметров Telegram API.</summary>
public sealed class ProtectedTelegramApiCredentialsStore(string dataDirectory)
    : ITelegramApiCredentialsStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(dataDirectory, "telegram.api.dpapi");

    public async Task<bool> IsConfiguredAsync(CancellationToken cancellationToken = default)
        => await LoadAsync(cancellationToken) is not null;

    public async Task<TelegramApiCredentials?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            var protectedBytes = await File.ReadAllBytesAsync(_path, cancellationToken);
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(
                    protectedBytes, null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException(
                    "Не удалось прочитать сохранённые API-ключи. Проверьте учётную запись Windows.", ex);
            }
            try
            {
                var value = JsonSerializer.Deserialize<TelegramApiCredentials>(decrypted);
                if (value is null || value.ApiId <= 0 || string.IsNullOrEmpty(value.ApiHash) || value.ApiHash.Length != 32 ||
                    !value.ApiHash.All(Uri.IsHexDigit))
                {
                    throw new InvalidDataException("Файл API-ключей повреждён.");
                }
                return value;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Файл API-ключей повреждён.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(TelegramApiCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var raw = JsonSerializer.SerializeToUtf8Bytes(credentials);
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(
                    raw, null, DataProtectionScope.CurrentUser);
            }
            finally { CryptographicOperations.ZeroMemory(raw); }
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
        finally { _gate.Release(); }
    }
}
