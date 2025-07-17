
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.OnlineAPIs.CloudMusicAPI;

public class CloudMusicSearchHelper
{
    private static readonly NeteaseCloudMusicApi _api = new();
    private const byte Limit = 10;

    private static async Task<JsonDocument> GetJsonElement(string keyWords) {
        var (_, result) = await _api.RequestAsync(
            CloudMusicApiProviders.Search,
            new Dictionary<string, string>
            {
                    { "keywords", keyWords },
                    { "limit",Limit.ToString() },
                    { "offset", "0" },
            }
        );
        JsonDocument document = JsonDocument.Parse(result.ToJsonString());        
        return document;
    }


    public static async Task<byte[]> GetSongAlbum(string title,string album,string author)
    {
        try {
            string keyWords = album + " " + author;
            using JsonDocument document = await GetJsonElement(keyWords);
            JsonElement root = document.RootElement;
            string albumId = SearchForAlbumId(root,album,author);
            string albumcoverUrl = await GetAlbumUrl(albumId);
            return await _api.GetImageBytesFromUrlAsync(albumcoverUrl);
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    public static async Task<string> GetSongLyrics(string title, string album, string author,CancellationToken cancellationToken = default)
    {
        try {
            string keyWords = title + " " + album + " " + author;
            cancellationToken.ThrowIfCancellationRequested();
            using JsonDocument document = await GetJsonElement(keyWords);
            JsonElement root = document.RootElement;
            Debug.WriteLine(root.ToString());
            cancellationToken.ThrowIfCancellationRequested();
            string songId = SearchForSongId(root, title, author);
            cancellationToken.ThrowIfCancellationRequested();
            var lyrics = await GetLyricsUrl(songId);
            cancellationToken.ThrowIfCancellationRequested();
            return lyrics;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private static string SearchForSongId(JsonElement root,string title,string artist)
    {
        try
        {
            JsonElement songsArray = root.GetProperty("result").GetProperty("songs");
            foreach (JsonElement songElement in songsArray.EnumerateArray())
            {
                string songName = songElement.GetProperty("name").GetString();
                if (string.Equals(songName, title, StringComparison.OrdinalIgnoreCase))
                {
                    JsonElement artistsArray = songElement.GetProperty("artists");
                    foreach (JsonElement artistElement in artistsArray.EnumerateArray())
                    {
                        string artistName = artistElement.GetProperty("name").GetString();
                        if (string.Equals(artistName, artist, StringComparison.OrdinalIgnoreCase))
                        {
                            return songElement.GetProperty("id").ToString();
                        }
                    }
                    return songElement.GetProperty("id").ToString();
                }
            }
            return songsArray[0].GetProperty("id").ToString();
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private static string SearchForAlbumId(JsonElement root, string album, string artist)
    {
        try
        {
            JsonElement songsArray = root.GetProperty("result").GetProperty("songs");
            foreach (JsonElement songElement in songsArray.EnumerateArray())
            {
                JsonElement albumElement = songElement.GetProperty("album");
                string albumName = albumElement.GetProperty("name").GetString();
                if (string.Equals(albumName, album, StringComparison.OrdinalIgnoreCase))
                {
                    JsonElement artistsArray = songElement.GetProperty("artists");
                    foreach (JsonElement artistElement in artistsArray.EnumerateArray())
                    {
                        string artistName = artistElement.GetProperty("name").GetString();
                        if (string.Equals(artistName, artist, StringComparison.OrdinalIgnoreCase))
                        {
                            return albumElement.GetProperty("id").ToString();
                        }
                    }
                    return albumElement.GetProperty("id").ToString();
                }
            }
            return songsArray[0].GetProperty("album").GetProperty("id").ToString();
        }
        catch (Exception ex)
        {
            return null;
        }
    }

    private static async Task<string> GetLyricsUrl(string songId)
    {
        if (songId != null)
        {
            var api = new NeteaseCloudMusicApi();
            var (_, lyricResult) = await api.RequestAsync(
                 CloudMusicApiProviders.Lyric,
                 new Dictionary<string, string> { { "id", $"{songId}" } }
             );
            string lyrics = string.Empty;
            lyrics = (string)lyricResult["lrc"]!["lyric"]!;
            if (AppData.systemLanguage == "zh") {
                try
                {
                    lyrics += (string)lyricResult["tlyric"]!["lyric"]!;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("No translated lyrics found.");
                }
            }
            return lyrics;
        }
        else { 
            return null;
        }       
    }

    public static async Task<string> GetAlbumUrl(string albumId)
    {
        if (albumId != null) {
            var api = new NeteaseCloudMusicApi();
            var (_, albumResult) = await api.RequestAsync(
                CloudMusicApiProviders.Album,
                new Dictionary<string, string> { { "id", $"{albumId}" } }
            );
            return (string)albumResult["album"]!["picUrl"]!;
        }
        else
        {
            return null;
        }        
    }
    
}
