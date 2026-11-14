using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZenMusic.Tagging;

namespace ZenMusic.Library;

public static class LibraryScanner
{
    private const int RecallOnDataAccess = 0x400000;

    public static async Task<IReadOnlyList<TrackItem>> ScanFolderAsync(
        string folder,
        CancellationToken token,
        Action<IReadOnlyList<TrackItem>>? onBatch = null,
        IReadOnlyDictionary<string, CachedTrackEntry>? cache = null)
    {
        return await Task.Run<IReadOnlyList<TrackItem>>(() =>
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return Array.Empty<TrackItem>();

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true
            };

            var files = Directory.EnumerateFiles(folder, "*", options)
                .Where(MusicLibrary.IsSupportedExtension)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var results = new TrackItem?[files.Count];
            var batchBuffer = new List<TrackItem>(100);
            object batchLock = new();

            try
            {
                Parallel.For(0, files.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = token
                }, i =>
                {
                    if (token.IsCancellationRequested) return;
                    string path = files[i];
                    bool isCloudOnly = false;
                    long fileSize = 0;
                    long lastWriteTicks = 0;
                    try
                    {
                        var info = new FileInfo(path);
                        isCloudOnly = ((int)info.Attributes & RecallOnDataAccess) != 0;
                        fileSize = info.Length;
                        lastWriteTicks = info.LastWriteTimeUtc.Ticks;
                    }
                    catch { return; }

                    TrackItem item;
                    if (cache != null &&
                        cache.TryGetValue(path, out var cached) &&
                        cached.LastWriteUtcTicks == lastWriteTicks &&
                        cached.FileSize == fileSize)
                    {
                        item = LibraryCache.ToTrackItem(cached, i + 1, isCloudOnly);
                    }
                    else
                    {
                        item = MusicLibrary.ReadTrackMetadata(path, i + 1, isCloudOnly);
                        if (cache != null && cache.TryGetValue(path, out var prior))
                        {
                            item.PlayCount = prior.PlayCount;
                            item.LastPlayedUtc = prior.LastPlayedUtc;
                            if (item.Rating == null && prior.Rating != null)
                                item.Rating = prior.Rating;
                        }
                    }
                    results[i] = item;

                    if (onBatch != null)
                    {
                        List<TrackItem>? flush = null;
                        lock (batchLock)
                        {
                            batchBuffer.Add(item);
                            if (batchBuffer.Count >= 80)
                            {
                                flush = new List<TrackItem>(batchBuffer);
                                batchBuffer.Clear();
                            }
                        }
                        if (flush != null) onBatch(flush);
                    }
                });
            }
            catch (OperationCanceledException) { }

            if (onBatch != null && batchBuffer.Count > 0 && !token.IsCancellationRequested)
            {
                onBatch(new List<TrackItem>(batchBuffer));
            }

            return results.Where(r => r != null).Cast<TrackItem>().ToList();
        }, token);
    }
}
