using System;
using System.IO;

namespace ZenMusic;

/// <summary>
/// Resolves application data paths. Supports Portable Mode if a "Data" folder or "portable.marker"
/// exists next to ZenMusic.exe; otherwise stores state in %LocalAppData%\ZenMusic.
/// </summary>
public static class AppPaths
{
    public static string LocalDirectory
    {
        get
        {
            string exeDir = AppContext.BaseDirectory;
            string portableData = Path.Combine(exeDir, "Data");
            if (Directory.Exists(portableData) || File.Exists(Path.Combine(exeDir, "portable.marker")))
            {
                Directory.CreateDirectory(portableData);
                return portableData;
            }

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZenMusic");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(LocalDirectory, "settings.json");
    public static string LibraryCachePath => Path.Combine(LocalDirectory, "library-cache.json");
}
