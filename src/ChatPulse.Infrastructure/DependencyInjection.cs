using ChatPulse.Application.Services;
using ChatPulse.Infrastructure.Persistence;
using ChatPulse.Infrastructure.Services;
using ChatPulse.Infrastructure.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatPulse.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        services.AddDbContext<ChatPulseDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath}"));

        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddSingleton<ITelegramAuthenticationClient>(
            _ => new WTelegramAuthenticationClient(Path.GetDirectoryName(databasePath)!));

        return services;
    }
}
