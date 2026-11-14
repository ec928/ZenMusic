using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace ZenMusic;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += App_UnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => LogCrash(a.ExceptionObject?.ToString());
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => LogCrash(a.Exception?.ToString());
    }

    private static void LogCrash(string? msg)
    {
        try
        {
            string logFile = Path.Combine(AppPaths.LocalDirectory, "crash.log");
            File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\n\n");
        }
        catch { }
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception?.ToString());
        e.Handled = false;
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
