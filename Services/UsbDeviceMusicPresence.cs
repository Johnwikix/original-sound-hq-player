using System;
using System.Collections.Generic;
using System.IO;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

internal static class UsbDeviceMusicPresence
{
    internal static void Update(IReadOnlyList<Music> songs, IReadOnlyList<UsbDeviceMusic> onDevice,
        IReadOnlyCollection<string>? changedTitles = null, IReadOnlyCollection<string>? changedFileNames = null)
    {
        HashSet<string>? titles = changedTitles is null ? null : new(changedTitles, StringComparer.Ordinal);
        var changedFiles = new HashSet<string>(changedFileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var changedFileLookup = changedFiles.GetAlternateLookup<ReadOnlySpan<char>>();
        IReadOnlyList<Music> affected = songs;
        HashSet<string>? neededTitles = null;
        var neededFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (titles is not null)
        {
            var selected = new List<Music>();
            neededTitles = new(StringComparer.Ordinal);
            foreach (var music in songs)
            {
                if (!titles.Contains(music.Title)
                    && !changedFileLookup.Contains(Path.GetFileNameWithoutExtension(music.Path.AsSpan()))) continue;
                selected.Add(music);
                neededTitles.Add(music.Title);
                neededFiles.Add(Path.GetFileNameWithoutExtension(music.Path));
            }
            affected = selected;
        }
        if (affected.Count == 0) return;
        var groups = new Dictionary<string, List<UsbDeviceMusic>>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var neededFileLookup = neededFiles.GetAlternateLookup<ReadOnlySpan<char>>();
        var fileNameLookup = fileNames.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var record in onDevice)
        {
            var fileName = Path.GetFileNameWithoutExtension(record.Path.AsSpan());
            // 只限制待更新歌曲；其标题下所有设备记录仍需参与完整匹配。
            if (neededTitles is null || neededTitles.Contains(record.Title))
            {
                if (!groups.TryGetValue(record.Title, out var group)) groups.Add(record.Title, group = []);
                group.Add(record);
            }
            if ((neededTitles is null || neededFileLookup.Contains(fileName)) && !fileNameLookup.Contains(fileName))
                fileNames.Add(fileName.ToString());
        }
        foreach (var music in affected)
        {
            var fileName = Path.GetFileNameWithoutExtension(music.Path.AsSpan());
            int flag = 0;
            if (groups.TryGetValue(music.Title, out var matches))
            {
                flag = 1;
                foreach (var record in matches)
                {
                    if (record.ScanMetadataRead != false && music.Author == record.Author && music.Album == record.Album
                        && string.Equals(music.Extension, record.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        flag = 2;
                        break;
                    }
                }
            }
            // MTP 缺少标签时可标记同文件名候选；文件名碰撞不能证明歌曲/版本完全一致。
            if (flag == 0 && fileNameLookup.Contains(fileName)) flag = 1;
            music.IsExistOnDevice = flag;
        }
    }
}
