using System.IO;
using System.Windows;
using ChatPulse.Application;
using ChatPulse.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ChatPulse.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChatPulse", "Data");
        Directory.CreateDirectory(dataDirectory);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.AddDebug();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(
            Path.Combine(dataDirectory, "chatpulse.db"));
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        base.OnExit(e);
    }
}
