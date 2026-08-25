using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils
{
    public static class ArtistHelper
    {
        public static string[] GetArtistNames(string? author)
        {
            if (string.IsNullOrWhiteSpace(author))
                return [author?.Trim() ?? ""];

            var splitters = AppSettings.ArtistSplitters;
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

        public static bool IsMusicByArtist(Music music, string artistName)
        {
            if (music is null) return false;
            var names = GetArtistNames(music.Author);
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Equals(artistName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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
    }
}
