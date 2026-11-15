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
