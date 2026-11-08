using System.IO;
using System.Windows;
using ChatPulse.Application;
using ChatPulse.Application.Services;
using ChatPulse.Desktop.Settings;
using ChatPulse.Desktop.ViewModels;
using ChatPulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPulse.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ApplicationInstanceLock? _instanceLock;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Асинхронная загрузка настроек до показа первого окна.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            var dataDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChatPulse", "Data");
            Directory.CreateDirectory(dataDirectory);
            if (!ApplicationInstanceLock.TryAcquire(dataDirectory, out _instanceLock))
            {
                MessageBox.Show("ChatPulse уже запущен. Закройте другое окно приложения перед повторным запуском.",
                    "ChatPulse", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            var builder = Host.CreateApplicationBuilder();
            builder.Logging.AddDebug();
            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(Path.Combine(dataDirectory, "chatpulse.db"));
            builder.Services.AddSingleton<ISettingsService, JsonSettingsService>();
            builder.Services.AddSingleton<IChatConfigurationStore, SettingsChatConfigurationStore>();
            builder.Services.AddSingleton<TelegramSetupViewModel>();
            builder.Services.AddSingleton<FirstChatSetupViewModel>();
            builder.Services.AddSingleton<MainWindow>();

            _host = builder.Build();
            await _host.StartAsync();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            await mainWindow.InitializeAsync();
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Не удалось запустить ChatPulse: {exception.Message}",
                "ChatPulse — Ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.StopAsync().GetAwaiter().GetResult();
        }
        finally
        {
            _host?.Dispose();
            _instanceLock?.Dispose();
            base.OnExit(e);
        }
    }
}
