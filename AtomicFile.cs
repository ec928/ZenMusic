using System;
using System.IO;
using System.Threading;

namespace ZenMusic;

/// <summary>
/// Writes a file atomically via a temporary sibling so a crash or power cut leaves
/// either the previous intact file or the new one, never a truncated half-write.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            File.WriteAllText(temp, text);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (attempt < 12 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(30);
                }
            }
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
