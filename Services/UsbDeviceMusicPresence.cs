using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

internal static class UsbDeviceMusicPresence
{
    internal static void Update(IReadOnlyList<Music> songs, IReadOnlyList<UsbDeviceMusic> onDevice,
        IReadOnlyCollection<string>? changedTitles = null)
    {
        HashSet<string>? titles = changedTitles is null ? null : new(changedTitles, StringComparer.Ordinal);
        var groups = new Dictionary<string, List<UsbDeviceMusic>>(StringComparer.Ordinal);
        foreach (var record in onDevice)
        {
            if (titles is not null && !titles.Contains(record.Title)) continue;
            if (!groups.TryGetValue(record.Title, out var group)) groups.Add(record.Title, group = []);
            group.Add(record);
        }
        foreach (var music in songs)
        {
            if (titles is not null && !titles.Contains(music.Title)) continue;
            int flag = 0;
            if (groups.TryGetValue(music.Title, out var matches))
            {
                flag = 1;
                foreach (var record in matches)
                {
                    if (music.Author == record.Author && music.Album == record.Album
                        && string.Equals(music.Extension, record.Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        flag = 2;
                        break;
                    }
                }
            }
            music.IsExistOnDevice = flag;
        }
    }
}
