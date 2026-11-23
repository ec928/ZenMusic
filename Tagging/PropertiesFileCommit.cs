using System;
using System.IO;

namespace ZenMusic.Tagging;

/// <summary>
/// One properties save: tags, then Date Added, then a rename.
/// Tag writes put the previous write time back, so the new Date Added has to be set after that.
/// The caller reopens <see cref="PropertiesFileCommitResult.ResumePath"/> once.
/// </summary>
internal static class PropertiesFileCommit
{
    public static PropertiesFileCommitResult Apply(
        string path,
        Func<(bool Ok, string? Error)> writeTags,
        DateTime? dateAddedUtc,
        string? renameTo)
    {
        var (ok, err) = writeTags();
        if (!ok)
            return new PropertiesFileCommitResult(false, err, false, null, false, null, path);

        string resume = path;
        bool dateWritten = false;
        string? dateError = null;
        if (dateAddedUtc.HasValue)
        {
            try
            {
                File.SetLastWriteTimeUtc(resume, dateAddedUtc.Value);
                dateWritten = true;
            }
            catch (Exception ex)
            {
                dateError = ex.Message;
            }
        }

        bool renamed = false;
        string? renameError = null;
        if (!string.IsNullOrWhiteSpace(renameTo) &&
            !string.Equals(renameTo, resume, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Move(resume, renameTo);
                resume = renameTo;
                renamed = true;
            }
            catch (Exception ex)
            {
                renameError = ex.Message;
            }
        }

        return new PropertiesFileCommitResult(true, null, dateWritten, dateError, renamed, renameError, resume);
    }
}

internal readonly record struct PropertiesFileCommitResult(
    bool TagsSaved,
    string? TagError,
    bool DateWritten,
    string? DateError,
    bool Renamed,
    string? RenameError,
    string ResumePath);
