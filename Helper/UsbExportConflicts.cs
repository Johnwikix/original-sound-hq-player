using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.Helper;

/// <summary>写入前检查最终音频及歌词路径；冲突项不开始复制，正常的同源重发仍可覆盖。</summary>
internal static class UsbExportConflicts
{
    private sealed class TargetOwners(int index, bool audio)
    {
        internal int First { get; } = index;
        internal bool Audio { get; } = audio;
        internal List<int> Indices { get; } = [index];
        internal bool Conflicting { get; set; }
    }

    internal static string?[] Find(IList<Music> songs, string? format, IReadOnlyList<UsbDeviceMusic>? existing,
        bool[] available, CancellationToken token)
    {
        var conflicts = new string?[songs.Count];
        var owners = new Dictionary<string, TargetOwners>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < songs.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!available[i]) continue;
            var music = songs[i];
            var targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            AddTargets(targets, UsbExportPaths.GetRelativeFilePath(music, music.Extension));
            if (format is not null)
                AddTargets(targets, UsbExportPaths.GetRelativeFilePath(music, AudioConverterService.GetExtensionForFormat(format)));
            foreach (var target in targets)
            {
                string path = target.Key;
                if (!owners.TryGetValue(path, out var group))
                {
                    owners.Add(path, new TargetOwners(i, target.Value));
                    continue;
                }
                bool sameOwner = target.Value || group.Audio
                    ? SameSource(music, songs[group.First]) : SameLyricsOwner(music, songs[group.First]);
                if (group.Conflicting || !sameOwner)
                {
                    conflicts[i] ??= path;
                    if (!group.Conflicting)
                        foreach (int previous in group.Indices) conflicts[previous] ??= path;
                    group.Conflicting = true;
                }
                group.Indices.Add(i);
            }
        }

        if (existing is null) return conflicts;
        var latest = new Dictionary<string, UsbDeviceMusic>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in existing)
        {
            token.ThrowIfCancellationRequested();
            string key = UsbExportPaths.GetStoragePathKey(record.Path);
            if (!latest.TryGetValue(key, out var previous) || record.Id > previous.Id) latest[key] = record;
        }
        foreach (var pair in latest)
        {
            token.ThrowIfCancellationRequested();
            var record = pair.Value;
            var targets = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            AddTargets(targets, pair.Key);
            foreach (var target in targets)
            {
                string path = target.Key;
                if (!owners.TryGetValue(path, out var group)) continue;
                foreach (int index in group.Indices)
                {
                    var music = songs[index];
                    bool sameName = string.Equals(Path.GetFileNameWithoutExtension(music.Path), Path.GetFileNameWithoutExtension(record.Path),
                        StringComparison.OrdinalIgnoreCase);
                    bool hasSource = !string.IsNullOrEmpty(record.ExportSourceIdentity);
                    bool sameSource = hasSource
                        && string.Equals(UsbExportPaths.GetSourceIdentity(music), record.ExportSourceIdentity, StringComparison.OrdinalIgnoreCase);
                    bool sameMetadata = record.ScanMetadataRead != false && music.Title == record.Title
                        && music.Author == record.Author && music.Album == record.Album;
                    // 音频覆盖要求同源；只有歌词路径相交时，保留同曲不同格式共享侧车的既有语义。
                    bool audioOwnerMatches = hasSource ? sameSource : sameMetadata;
                    bool sameOwner = sameName && (target.Value || group.Audio
                        ? audioOwnerMatches
                        : sameSource || sameMetadata);
                    if (!sameOwner) conflicts[index] ??= path;
                }
            }
        }
        return conflicts;
    }

    private static bool SameSource(Music left, Music right)
        => string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase)
            && left.Title == right.Title && left.Author == right.Author && left.Album == right.Album;

    private static bool SameLyricsOwner(Music left, Music right)
        => left.Title == right.Title && left.Author == right.Author && left.Album == right.Album
            && string.Equals(Path.GetFileNameWithoutExtension(left.Path), Path.GetFileNameWithoutExtension(right.Path), StringComparison.OrdinalIgnoreCase);

    private static void AddTargets(Dictionary<string, bool> targets, string audioPath)
    {
        targets[audioPath] = true;
        targets.TryAdd(Path.ChangeExtension(audioPath, ".lrc"), false);
        string stem = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
        targets.TryAdd(stem + "_Translated.lrc", false);
        targets.TryAdd(stem + "_Pronunciation.lrc", false);
    }
}
