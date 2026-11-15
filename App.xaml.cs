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
        var launchFiles = ExtractLaunchAudioFiles(args?.Arguments);
        _window = new MainWindow(launchFiles);
        _window.Activate();
    }

    private static System.Collections.Generic.IReadOnlyList<string> ExtractLaunchAudioFiles(string? launchArgs)
    {
        var files = new System.Collections.Generic.List<string>();
        foreach (string arg in Environment.GetCommandLineArgs().Skip(1))
        {
            TryAddAudioFile(arg, files);
        }

        if (files.Count == 0 && !string.IsNullOrWhiteSpace(launchArgs))
        {
            TryAddAudioFile(launchArgs, files);
        }

        return files;
    }

    private static void TryAddAudioFile(string? candidate, System.Collections.Generic.List<string> target)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return;
        try
        {
            string full = Path.GetFullPath(candidate.Trim().Trim('"'));
            if (File.Exists(full) && Tagging.MusicLibrary.IsSupportedExtension(full) &&
                !target.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(full);
            }
        }
        catch { }
    }
}
