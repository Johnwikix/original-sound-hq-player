using Lyricify.Lyrics.Models;
using Lyricify.Lyrics.Parsers.Models;
using Lyricify.Lyrics.Serialization;
using System.Text.Json;

namespace Lyricify.Lyrics.Parsers
{
    public static class MusixmatchParser
    {
        public static LyricsData? Parse(string rawJson)
        {
            return Parse(rawJson, false);
        }

        /// <param name="ignoreSyllable">忽略逐字歌词</param>
        public static LyricsData? Parse(string rawJson, bool ignoreSyllable)
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var jsonObj = document.RootElement;
            var calls = jsonObj.Property("message").Property("body").Property("macro_calls");
            if (calls.ValueKind != JsonValueKind.Object) return null;

            static bool CheckHeader200(JsonElement getObj)
            {
                var status = getObj.Property("message").Property("header").Property("status_code");
                return status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int code) && code == 200;
            }

            var track_get = calls.Property("track.richsync.get").AsObject();
            if (!ignoreSyllable && CheckHeader200(track_get))
            {
                var lyrics = track_get.Property("message").Property("body").Property("richsync").Property("richsync_body").ScalarText();

                if (!string.IsNullOrEmpty(lyrics) && LyricsJson.Deserialize<List<RichSyncedLine>>(lyrics) is List<RichSyncedLine> list)
                {
                    var lines = new List<ILineInfo>();
                    foreach (var line in list)
                    {
                        var syllables = new List<SyllableInfo>();
                        var start = (int)(line.TimeStart * 1000);
                        for (int i = 0; i < line.Words.Count; i++)
                        {
                            syllables.Add(new()
                            {
                                StartTime = start + (int)(line.Words[i].Position * 1000),
                                EndTime = i + 1 < line.Words.Count ? start + (int)(line.Words[i + 1].Position * 1000) : (int)(line.TimeEnd * 1000),
                                Text = line.Words[i].Chars,
                            });
                        }
                        lines.Add(new SyllableLineInfo()
                        {
                            Syllables = syllables.Cast<ISyllableInfo>().ToList(),
                        });
                    }

                    var lyricsData = new LyricsData
                    {
                        File = new(),
                        Lines = lines,
                        TrackMetadata = new TrackMetadata(),
                    };
                    lyricsData.File.Type = LyricsTypes.Musixmatch;
                    lyricsData.File.SyncTypes = SyncTypes.SyllableSynced;
                    var language = track_get.Property("message").Property("body").Property("richsync").Property("richssync_language").ScalarText()
                        ?? track_get.Property("message").Property("body").Property("richsync").Property("richsync_language").ScalarText();
                    if (language is not null)
                    {
                        lyricsData.TrackMetadata.Language = new() { language };
                    }
                    return lyricsData;
                }
            }

            track_get = calls.Property("track.subtitles.get").AsObject();
            if (CheckHeader200(track_get))
            {
                var list = track_get.Property("message").Property("body").Property("subtitle_list");
                if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                {
                    var subtitle = list[0].Property("subtitle").Property("subtitle_body").ScalarText();
                    if (!string.IsNullOrEmpty(subtitle))
                    {
                        var lines = LrcParser.ParseLyrics(subtitle);
                        var lyricsData = new LyricsData
                        {
                            File = new(),
                            Lines = lines,
                            TrackMetadata = new TrackMetadata(),
                        };
                        lyricsData.File.Type = LyricsTypes.Musixmatch;
                        lyricsData.File.SyncTypes = SyncTypes.LineSynced;
                        var language = list[0].Property("subtitle").Property("subtitle_language").ScalarText();
                        if (language is not null)
                        {
                            lyricsData.TrackMetadata.Language = new() { language };
                        }
                        return lyricsData;
                    }
                }
            }

            track_get = calls.Property("track.lyrics.get").AsObject();
            if (CheckHeader200(track_get))
            {
                var lyrics = track_get.Property("message").Property("body").Property("lyrics").Property("lyrics_body").ScalarText();

                if (!string.IsNullOrEmpty(lyrics))
                {
                    var list = lyrics.Trim()
                        .Split('\n')
                        .Select(line => new LineInfo { Text = line })
                        .Cast<ILineInfo>()
                        .ToList();

                    var lyricsData = new LyricsData
                    {
                        File = new(),
                        Lines = list,
                    };
                    lyricsData.File.Type = LyricsTypes.Musixmatch;
                    lyricsData.File.SyncTypes = SyncTypes.Unsynced;
                    return lyricsData;
                }
            }

            return null;
        }
    }
}
