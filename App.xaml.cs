using Microsoft.UI.Xaml;
using System;
using System.IO;

namespace ZenMusic;

public sealed class SnapshotRequest
{
    public string OutputPath { get; init; } = string.Empty;
    public string? ViewMode { get; init; }
    public string? AlbumFilter { get; init; }
    public string? SearchQuery { get; init; }
    public int? ScrollQueueToIndex { get; init; }
    public string? OpenDialog { get; init; }
}

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
        var snapshot = ParseSnapshotRequest();
        var launchFiles = snapshot == null ? ExtractLaunchAudioFiles(args?.Arguments) : Array.Empty<string>();
        _window = new MainWindow(launchFiles, snapshot);
        _window.Activate();
    }

    private static SnapshotRequest? ParseSnapshotRequest()
    {
        var argv = Environment.GetCommandLineArgs();
        string? outPath = null;
        string? view = null;
        string? album = null;
        string? search = null;
        int? scrollQueue = null;
        string? dialog = null;

        for (int i = 1; i < argv.Length; i++)
        {
            string a = argv[i];
            if (string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                outPath = argv[++i];
            else if (string.Equals(a, "--snapshot-view", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                view = argv[++i];
            else if (string.Equals(a, "--snapshot-album", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                album = argv[++i];
            else if (string.Equals(a, "--snapshot-search", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                search = argv[++i];
            else if (string.Equals(a, "--snapshot-scroll-queue", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length && int.TryParse(argv[++i], out int qIdx))
                scrollQueue = qIdx;
            else if (string.Equals(a, "--snapshot-dialog", StringComparison.OrdinalIgnoreCase) && i + 1 < argv.Length)
                dialog = argv[++i];
        }

        return string.IsNullOrWhiteSpace(outPath) ? null : new SnapshotRequest
        {
            OutputPath = Path.GetFullPath(outPath),
            ViewMode = view,
            AlbumFilter = album,
            SearchQuery = search,
            ScrollQueueToIndex = scrollQueue,
            OpenDialog = dialog
        };
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
