using System;
using System.Collections.Generic;
using System.Threading;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils
{
    public static class ArtistHelper
    {
        private const int NameCacheLimit = 4096;
        private static readonly Lock s_cacheLock = new();
        private static string[] s_splittersSnapshot = AppSettings.ArtistSplitters;
        private static readonly Dictionary<string, string[]> s_nameCache = new(1024, StringComparer.Ordinal);

        public static string[] GetArtistNames(string? author)
        {
            if (string.IsNullOrWhiteSpace(author))
                return [author?.Trim() ?? ""];

            var splitters = AppSettings.ArtistSplitters;
            lock (s_cacheLock)
            {
                if (!ReferenceEquals(splitters, s_splittersSnapshot))
                {
                    s_splittersSnapshot = splitters;
                    s_nameCache.Clear();
                }
                if (s_nameCache.TryGetValue(author, out var cached))
                    return cached;
            }

            var names = SplitCore(author, splitters);
            lock (s_cacheLock)
            {
                if (s_nameCache.Count >= NameCacheLimit)
                    s_nameCache.Clear();
                s_nameCache[author] = names;
            }
            return names;
        }

        public static bool IsMusicByArtist(Music music, string artistName)
        {
            if (music is null || string.IsNullOrWhiteSpace(artistName)) return false;
            var author = music.Author;
            if (string.IsNullOrEmpty(author)) return false;

            var splitters = AppSettings.ArtistSplitters;
            if (splitters.Length == 0)
                return author.AsSpan().Trim().Equals(artistName.AsSpan(), StringComparison.OrdinalIgnoreCase);

            if (author.Equals(artistName, StringComparison.OrdinalIgnoreCase))
                return true;

            ReadOnlySpan<char> s = author;
            int len = s.Length;
            int start = 0;
            int i = 0;
            while (i < len)
            {
                int sepLen = SeparatorAt(s, i, splitters);
                if (sepLen > 0)
                {
                    if (SegmentEquals(s[start..i], artistName)) return true;
                    i += sepLen;
                    start = i;
                    continue;
                }
                i++;
            }
            return SegmentEquals(s[start..len], artistName);
        }

        public static Music CreateArtistTile(Music source, string artistName)
        {
            return new Music
            {
                Id = source.Id,
                Path = source.Path,
                Title = source.Title,
                Author = artistName,
                Album = source.Album,
                FolderPath = source.FolderPath,
                LastLevelFolderPath = source.LastLevelFolderPath,
                Extension = source.Extension,
                ImageHash = source.ImageHash,
                Duration = source.Duration,
                Year = source.Year,
                CreateTime = source.CreateTime,
                UpdateTime = source.UpdateTime
            };
        }

        private static string[] SplitCore(string author, string[] splitters)
        {
            if (splitters.Length == 0)
                return [author.Trim()];

            List<string> parts = [author];
            foreach (var sep in splitters)
            {
                if (sep.Length == 0) continue;
                var next = new List<string>(parts.Count * 2);
                foreach (var part in parts)
                {
                    if (part.IndexOf(sep, StringComparison.Ordinal) < 0)
                    {
                        next.Add(part);
                        continue;
                    }
                    next.AddRange(part.Split(sep, StringSplitOptions.None));
                }
                parts = next;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                    names.Add(trimmed);
            }
            if (names.Count == 0)
                return [author.Trim()];
            return names.ToArray();
        }

        private static bool SegmentEquals(ReadOnlySpan<char> segment, string name)
        {
            return segment.Trim().Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        private static int SeparatorAt(ReadOnlySpan<char> s, int i, string[] splitters)
        {
            int best = 0;
            for (int k = 0; k < splitters.Length; k++)
            {
                var sep = splitters[k];
                int sepLen = sep.Length;
                if (sepLen <= best) continue;
                if (sepLen <= s.Length - i && s.Slice(i, sepLen).SequenceEqual(sep.AsSpan()))
                    best = sepLen;
            }
            return best;
        }
    }
}
