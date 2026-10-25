using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed class LastFmApiException(int code, bool retryable) : Exception($"Last.fm request failed ({code}).")
{
    public int Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

/// <summary>Only signed HTTPS requests. Credentials and server response bodies never enter logs.</summary>
public sealed class LastFmApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    public LastFmApiClient() : this(new HttpClient(), new Uri("https://ws.audioscrobbler.com/2.0/")) { }
    public LastFmApiClient(HttpClient http, Uri endpoint)
    {
        _http = http;
        _endpoint = endpoint;
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OriginalSoundPlayer/1.0");
    }

    public async Task<string> GetTokenAsync(LastFmCredentials credentials, CancellationToken token)
    {
        using var reply = await RequestAsync(credentials, "auth.getToken", [], null, token).ConfigureAwait(false);
        return reply.RootElement.GetProperty("token").GetString() ?? throw new JsonException();
    }

    public async Task<LastFmAccount> GetSessionAsync(LastFmCredentials credentials, string authToken, CancellationToken token)
    {
        using var reply = await RequestAsync(credentials, "auth.getSession", new() { ["token"] = authToken }, null, token).ConfigureAwait(false);
        var session = reply.RootElement.GetProperty("session");
        string name = session.GetProperty("name").GetString() ?? "";
        string key = session.GetProperty("key").GetString() ?? "";
        if (name.Length == 0 || key.Length == 0) throw new JsonException();
        return new(name, key);
    }

    public async Task NowPlayingAsync(LastFmIdentity identity, LastFmTrack track, CancellationToken token)
    {
        using var reply = await RequestAsync(identity.Credentials, "track.updateNowPlaying", TrackParameters(track),
            identity.Account!.SessionKey, token).ConfigureAwait(false);
    }

    public async Task SetLovedAsync(LastFmIdentity identity, LastFmTrack track, bool loved, CancellationToken token)
    {
        using var reply = await RequestAsync(identity.Credentials, loved ? "track.love" : "track.unlove",
            new() { ["artist"] = track.Artist, ["track"] = track.Title }, identity.Account!.SessionKey, token).ConfigureAwait(false);
    }

    // Delivery confirmation for an interrupted request only. Never imports remote state into the library.
    public async Task<bool> IsLovedAsync(LastFmIdentity identity, LastFmTrack track, CancellationToken token)
    {
        using var reply = await RequestAsync(identity.Credentials, "track.getInfo", new()
        {
            ["artist"] = track.Artist, ["track"] = track.Title, ["username"] = identity.Account!.Name
        }, null, token).ConfigureAwait(false);
        var item = reply.RootElement.GetProperty("track");
        if (item.ValueKind != JsonValueKind.Object) throw new JsonException("Unexpected track information.");
        int loved = ReadInteger(item.GetProperty("userloved"));
        return loved switch { 0 => false, 1 => true, _ => throw new JsonException("Unexpected loved state.") };
    }

    public async Task<LastFmRecentPage> RecentPlaysAsync(LastFmIdentity identity, long from, long to, int page, CancellationToken token)
    {
        using var reply = await RequestAsync(identity.Credentials, "user.getRecentTracks", new()
        {
            ["user"] = identity.Account!.Name, ["from"] = from.ToString(CultureInfo.InvariantCulture),
            ["to"] = to.ToString(CultureInfo.InvariantCulture), ["page"] = page.ToString(CultureInfo.InvariantCulture), ["limit"] = "200"
        }, null, token).ConfigureAwait(false);
        var recent = reply.RootElement.GetProperty("recenttracks");
        var result = new List<LastFmRecentPlay>();
        var tracks = recent.GetProperty("track");
        if (tracks.ValueKind == JsonValueKind.Array)
            foreach (var track in tracks.EnumerateArray()) Add(track);
        else if (tracks.ValueKind == JsonValueKind.Object) Add(tracks);
        else throw new JsonException();
        var total = recent.GetProperty("@attr").GetProperty("totalPages");
        if (!int.TryParse(total.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages) || pages < 0)
            throw new JsonException();
        return new(result, pages);

        void Add(JsonElement track)
        {
            // Some responses include a date even on the Now Playing item. It is never history.
            if (track.TryGetProperty("@attr", out var flags) && flags.TryGetProperty("nowplaying", out var nowPlaying)
                && string.Equals(nowPlaying.ToString(), "true", StringComparison.OrdinalIgnoreCase)) return;
            if (!track.TryGetProperty("date", out var date) || !date.TryGetProperty("uts", out var uts)) return;
            if (!long.TryParse(uts.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp)) throw new JsonException();
            if (timestamp < from || timestamp > to) return;
            string title = track.GetProperty("name").GetString() ?? "";
            var artist = track.GetProperty("artist");
            string name = artist.ValueKind == JsonValueKind.String ? artist.GetString() ?? "" : artist.GetProperty("#text").GetString() ?? "";
            if (title.Length != 0 && name.Length != 0) result.Add(new(title, name, timestamp));
        }
    }

    /// <returns>Whether Last.fm accepted the play. Metadata rejection is a successful HTTP response.</returns>
    public async Task<bool> ScrobbleAsync(LastFmIdentity identity, LastFmScrobble play, CancellationToken token)
    {
        var parameters = TrackParameters(play.Track);
        parameters["timestamp"] = play.StartedAt.ToString(CultureInfo.InvariantCulture);
        using var reply = await RequestAsync(identity.Credentials, "track.scrobble", parameters,
            identity.Account!.SessionKey, token).ConfigureAwait(false);
        try
        {
            var scrobbles = reply.RootElement.GetProperty("scrobbles");
            int accepted = ReadInteger(scrobbles.GetProperty("@attr").GetProperty("accepted"));
            if (accepted == 1) return true;
            if (accepted != 0) throw new JsonException("Unexpected single-scrobble acceptance count.");
            var item = scrobbles.GetProperty("scrobble");
            if (item.ValueKind == JsonValueKind.Array)
            {
                if (item.GetArrayLength() != 1) throw new JsonException("Unexpected single-scrobble response count.");
                item = item[0];
            }
            int ignored = ReadInteger(item.GetProperty("ignoredMessage").GetProperty("code"));
            // Daily quota is temporary; known metadata/time rejections cannot be repaired by retries.
            if (ignored == 5) throw new LastFmApiException(29, true);
            if (ignored is >= 1 and <= 4) return false;
            // Preserve the queue on contradictory or unknown responses instead of inventing an ACK.
            throw new JsonException("Unexpected Last.fm ignored code.");
        }
        catch (InvalidOperationException ex)
        {
            // JSON container/type errors describe a remote response, not a local storage failure.
            throw new JsonException("Invalid Last.fm scrobble response structure.", ex);
        }
    }

    private static int ReadInteger(JsonElement value)
    {
        // Last.fm's live JSON uses numbers here; older responses and clients also use strings.
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        throw new JsonException("Invalid Last.fm integer value.");
    }

    private static Dictionary<string, string> TrackParameters(LastFmTrack track)
    {
        var result = new Dictionary<string, string> { ["artist"] = track.Artist, ["track"] = track.Title };
        if (track.Album.Length != 0) result["album"] = track.Album;
        if (track.DurationSeconds > 0) result["duration"] = track.DurationSeconds.ToString(CultureInfo.InvariantCulture);
        return result;
    }

    internal static string Sign(SortedDictionary<string, string> parameters, string secret)
    {
        var text = new StringBuilder();
        foreach (var pair in parameters) text.Append(pair.Key).Append(pair.Value);
        text.Append(secret);
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private async Task<JsonDocument> RequestAsync(LastFmCredentials credentials, string method,
        Dictionary<string, string> values, string? session, CancellationToken token)
    {
        var parameters = new SortedDictionary<string, string>(values, StringComparer.Ordinal)
        {
            ["api_key"] = credentials.ApiKey, ["method"] = method
        };
        if (session is not null) parameters["sk"] = session;
        parameters["api_sig"] = Sign(parameters, credentials.SharedSecret);
        parameters["format"] = "json";
        using var content = new FormUrlEncodedContent(parameters);
        using var response = await _http.PostAsync(_endpoint, content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new LastFmApiException((int)response.StatusCode,
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Invalid Last.fm response root.");
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                int code = ReadInteger(error);
                throw new LastFmApiException(code, code is 8 or 11 or 16 or 29);
            }
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}
