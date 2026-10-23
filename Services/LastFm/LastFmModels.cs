using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed record LastFmCredentials(string ApiKey, string SharedSecret);
public sealed record LastFmAccount(string Name, string SessionKey);
public sealed record LastFmIdentity(LastFmCredentials Credentials, LastFmAccount? Account = null);
public sealed record LastFmTrack(string Title, string Artist, string Album, int DurationSeconds);
public sealed record LastFmScrobble(Guid Id, string ApiKey, string Account, LastFmTrack Track, long StartedAt);

public sealed record LastFmSavedState
{
    public int Version { get; init; } = 1;
    public bool Enabled { get; init; }
    public bool HasCredentials { get; init; }
    public List<LastFmScrobble> Pending { get; init; } = [];
}

public enum LastFmProblem { None, Network, Credentials, AuthorizationPending, AuthorizationExpired, SessionExpired, Rejected, Storage, QueueFull }

public sealed record LastFmStatus(string ApiKey = "", bool HasSecret = false, string UserName = "",
    bool IsAuthenticated = false, bool Enabled = false, int PendingCount = 0, bool AuthorizationPending = false,
    LastFmProblem Problem = LastFmProblem.None, long Revision = 0, bool IsReady = false);

[JsonSerializable(typeof(LastFmSavedState))]
[JsonSerializable(typeof(LastFmIdentity))]
internal partial class LastFmJsonContext : JsonSerializerContext;
