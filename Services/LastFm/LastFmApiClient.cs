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

    /// <returns>Whether Last.fm accepted the play. Metadata rejection is a successful HTTP response.</returns>
    public async Task<bool> ScrobbleAsync(LastFmIdentity identity, LastFmScrobble play, CancellationToken token)
    {
        var parameters = TrackParameters(play.Track);
        parameters["timestamp"] = play.StartedAt.ToString(CultureInfo.InvariantCulture);
        using var reply = await RequestAsync(identity.Credentials, "track.scrobble", parameters,
            identity.Account!.SessionKey, token).ConfigureAwait(false);
        var scrobbles = reply.RootElement.GetProperty("scrobbles");
        if (scrobbles.GetProperty("@attr").GetProperty("accepted").GetString() == "1") return true;
        var item = scrobbles.GetProperty("scrobble");
        if (item.ValueKind == JsonValueKind.Array) item = item[0];
        string? ignored = item.GetProperty("ignoredMessage").GetProperty("code").GetString();
        // Daily quota is temporary; malformed metadata and stale timestamps cannot be repaired by retries.
        if (ignored == "5") throw new LastFmApiException(29, true);
        return false;
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
        if (document.RootElement.TryGetProperty("error", out var error))
        {
            int code = error.GetInt32();
            document.Dispose();
            throw new LastFmApiException(code, code is 8 or 11 or 16 or 29);
        }
        return document;
    }

    public void Dispose() => _http.Dispose();
}
