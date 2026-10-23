using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed record LastFmCredentials(string ApiKey, string SharedSecret);
public sealed record LastFmAccount(string Name, string SessionKey);
public sealed record LastFmIdentity(LastFmCredentials Credentials, LastFmAccount? Account = null);
public sealed record LastFmTrack(string Title, string Artist, string Album, int DurationSeconds);
public sealed record LastFmScrobble(Guid Id, string ApiKey, string Account, LastFmTrack Track, long StartedAt,
    string? HistoryKey = null, long OriginalStartedAt = 0, bool Retimed = false);
public sealed record LastFmHistoryReceipt(string Account, string HistoryKey, string TrackKey = "",
    long OriginalStartedAt = 0, long SubmittedAt = 0, bool Retimed = false);
public sealed record LastFmAcknowledgement(LastFmScrobble Play, bool Accepted);
public sealed record LastFmHistoryMapping(string Account, string HistoryKey, string TrackKey,
    long OriginalStartedAt, long SubmittedAt, bool Accepted, bool Retimed, LastFmTrack? Track);
public readonly record struct LastFmAlias(string TrackKey, long SubmittedAt);
public sealed record LastFmAliasMatches(HashSet<LastFmAlias> Retimed, HashSet<LastFmAlias> Uncertain);

public sealed record LastFmSavedState
{
    public int Version { get; init; } = 2;
    public bool Enabled { get; init; }
    public bool HasCredentials { get; init; }
    public List<LastFmScrobble> Pending { get; init; } = [];
    public List<LastFmKnownPlay> Known { get; init; } = [];
    public List<LastFmHistoryReceipt> HistoryReceipts { get; init; } = [];
}

public enum LastFmProblem { None, Network, Credentials, AuthorizationPending, AuthorizationExpired, SessionExpired, Rejected, Storage, QueueFull }

public sealed record LastFmStatus(string ApiKey = "", bool HasSecret = false, string UserName = "",
    bool IsAuthenticated = false, bool Enabled = false, int PendingCount = 0, bool AuthorizationPending = false,
    LastFmProblem Problem = LastFmProblem.None, long Revision = 0, bool IsReady = false,
    LastFmHistoryImportResult? HistoryImport = null, int FavoritePendingCount = 0,
    LastFmFavoriteImportResult? FavoriteImport = null);

[JsonSerializable(typeof(LastFmSavedState))]
[JsonSerializable(typeof(LastFmIdentity))]
[JsonSerializable(typeof(LastFmTrack))]
internal partial class LastFmJsonContext : JsonSerializerContext;
