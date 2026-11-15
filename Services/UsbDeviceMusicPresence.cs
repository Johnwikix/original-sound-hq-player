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
        var groups = new Dictionary<string, List<UsbDeviceMusic>>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in onDevice)
        {
            var fileName = Path.GetFileNameWithoutExtension(record.Path.AsSpan());
            if (titles is not null && !titles.Contains(record.Title) && !changedFileLookup.Contains(fileName)) continue;
            if (!groups.TryGetValue(record.Title, out var group)) groups.Add(record.Title, group = []);
            group.Add(record);
            fileNames.Add(fileName.ToString());
        }
        var fileNameLookup = fileNames.GetAlternateLookup<ReadOnlySpan<char>>();
        foreach (var music in songs)
        {
            var fileName = Path.GetFileNameWithoutExtension(music.Path.AsSpan());
            if (titles is not null && !titles.Contains(music.Title) && !changedFileLookup.Contains(fileName)) continue;
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
