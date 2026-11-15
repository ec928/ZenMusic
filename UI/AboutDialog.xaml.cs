using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Reflection;

namespace ZenMusic.UI;

/// <summary>
/// About dialog displaying the runtime version, feature overview, privacy/portability summary,
/// and direct links to the GitHub repository and issue tracker.
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    private const string RepositoryUrl = "https://github.com/ec928/ZenMusic";
    private const string NewIssueUrl = RepositoryUrl + "/issues/new/choose";

    public AboutDialog()
    {
        this.InitializeComponent();
        VersionText.Text = $"Version {GetDisplayVersion()}";
        try
        {
            string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "ZenMusic.ico");
            if (System.IO.File.Exists(icon))
                AppIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icon));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[About] Could not load the icon: {ex.Message}");
        }
        SetTint(null);
    }

    /// <summary>
    /// Washes the header in the colour the main window is tinted with (the playing track's cover),
    /// or in amber when nothing is playing.
    /// </summary>
    public void SetTint(Windows.UI.Color? playing)
    {
        bool hasTrack = playing is { A: > 0 };
        Windows.UI.Color c = hasTrack ? playing!.Value : Windows.UI.Color.FromArgb(255, 0xE8, 0xA0, 0x2C);
        bool light = ActualTheme == ElementTheme.Light || RequestedTheme == ElementTheme.Light;
        var brush = new Microsoft.UI.Xaml.Media.LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1)
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb((byte)(light ? 0x66 : 0x80), c.R, c.G, c.B),
            Offset = 0
        });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Windows.UI.Color.FromArgb(0x10, c.R, c.G, c.B),
            Offset = 1
        });
        HeroTint.Background = brush;
        TintNote.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetDisplayVersion()
    {
        var asm = typeof(AboutDialog).Assembly;
        string? infoVersion = asm
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Split('+')[0];

        if (!string.IsNullOrWhiteSpace(infoVersion))
            return infoVersion;

        Version? v = asm.GetName().Version;
        return v == null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void ReportButton_Click(object sender, RoutedEventArgs e) => OpenUrl(NewIssueUrl);

    private void ProjectButton_Click(object sender, RoutedEventArgs e) => OpenUrl(RepositoryUrl);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => this.Hide();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[About] Could not open {url}: {ex.Message}");
        }
    }
}
