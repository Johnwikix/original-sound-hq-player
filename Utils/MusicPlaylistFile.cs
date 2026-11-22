using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils;

internal static class MusicPlaylistFile
{
    private const string CuePrefix = "#OSP-CUE:";

    internal static void WriteEntry(StringBuilder output, Music music)
    {
        string title = $"{music.Author} - {music.Title}".Replace('\r', ' ').Replace('\n', ' ');
        output.AppendLine(FormattableString.Invariant($"#EXTINF:{music.Duration.TotalSeconds:0.###},{title}"));
        if (music.IsCueTrack && music.SampleRate > 0)
        {
            output.AppendLine(FormattableString.Invariant($"{CuePrefix}{music.CueTrackNo}|{Path.GetFileName(music.CuePath)}"));
            output.AppendLine(FormattableString.Invariant($"#EXTVLCOPT:start-time={(double)music.StartSample / music.SampleRate:0.#########}"));
            output.AppendLine(FormattableString.Invariant($"#EXTVLCOPT:stop-time={(double)music.EndSample / music.SampleRate:0.#########}"));
        }
        output.AppendLine(music.Path);
    }

    internal static List<Music> Resolve(string content, IReadOnlyList<Music> library)
    {
        using var reader = new StringReader(content);
        if (!string.Equals(reader.ReadLine()?.TrimStart('\uFEFF'), "#EXTM3U", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("无效的m3u8文件格式，必须以#EXTM3U开头");
        var exact = new Dictionary<string, Music?>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new Dictionary<string, Music?>(StringComparer.OrdinalIgnoreCase);
        foreach (var music in library)
        {
            if (music.IsDetached || music.IsCueTrack && !music.CueMappingActive) continue;
            string suffix = music.IsCueTrack ? FormattableString.Invariant($"|{music.CueTrackNo}|{Path.GetFileName(music.CuePath)}") : "";
            Add(exact, music.Path + suffix, music);
            Add(fileNames, Path.GetFileName(music.Path) + suffix, music);
        }
        List<Music> result = [];
        string marker = "";
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith(CuePrefix, StringComparison.OrdinalIgnoreCase))
                marker = "|" + line[CuePrefix.Length..];
            else if (line.Length > 0 && line[0] != '#')
            {
                if (!exact.TryGetValue(line + marker, out var music))
                    fileNames.TryGetValue(Path.GetFileName(line) + marker, out music);
                if (music is not null) result.Add(music);
                marker = "";
            }
        }
        return result;
    }

    private static void Add(Dictionary<string, Music?> index, string key, Music music)
    {
        if (!index.TryAdd(key, music)) index[key] = null;
    }
}
