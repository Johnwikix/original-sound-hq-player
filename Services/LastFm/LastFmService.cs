using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

/// <summary>Owns authorization and a durable FIFO. No work starts in the constructor.</summary>
public sealed partial class LastFmService(LastFmApiClient api, ILastFmStore store, LastFmFavoriteStore? favorites = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _network = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource _identityRequests = new();
    private LastFmSavedState _saved = new();
    private LastFmIdentity? _identity;
    private string? _authToken;
    private DateTimeOffset _authExpires, _retryAt;
    private LastFmProblem _problem;
    private LastFmHistoryImportResult? _historyImport;
    private LastFmAcknowledgement? _acknowledged;
    private long _revision;
    private int _failures;
    private bool _ready, _blocked;
    private volatile bool _stopped;
    private Task? _worker, _stopTask;
    private LastFmStatus _status = new();
    public LastFmStatus Status => Volatile.Read(ref _status);
    public event Action<LastFmStatus>? Changed;

    private readonly double _retryUnitSeconds = 30;
    // A bounded timing seam for asynchronous error-path regressions; the application keeps the
    // public constructor and its original 60..900 second exponential retry policy.
    internal LastFmService(LastFmApiClient api, ILastFmStore store, LastFmFavoriteStore? favorites, TimeSpan retryUnit)
        : this(api, store, favorites)
    {
        if (retryUnit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryUnit));
        _retryUnitSeconds = retryUnit.TotalSeconds;
    }

    public Task StartAsync() => CommandAsync(async token =>
    {
        if (_ready) return;
        // A failed read is never replaced with an empty queue. The user can retry initialization.
        var saved = await store.LoadAsync(token).ConfigureAwait(false);
        // Fresh installs do not touch the Windows credential service for a disabled integration.
        var identity = saved.HasCredentials ? await store.ReadIdentityAsync(token).ConfigureAwait(false) : null;
        _saved = saved;
        _identity = identity;
        await RetainFavoritesAsync(identity).ConfigureAwait(false);
        _ready = true;
        _problem = LastFmProblem.None;
        Publish(true);
        _worker ??= WorkerAsync();
        Wake();
    }, requireReady: false);

    public Task ConfigureAsync(string apiKey, string sharedSecret) => CommandAsync(async token =>
    {
        apiKey = apiKey.Trim();
        sharedSecret = sharedSecret.Trim();
        if (sharedSecret.Length == 0 && apiKey == _identity?.Credentials.ApiKey) sharedSecret = _identity.Credentials.SharedSecret;
        if (!IsHexKey(apiKey) || !IsHexKey(sharedSecret)) throw new ArgumentException("Last.fm keys must contain 32 hexadecimal characters.");
        var credentials = new LastFmCredentials(apiKey, sharedSecret);
        if (_identity?.Credentials == credentials) return;
        CancelRequests();
        await DrainNetworkAsync(token).ConfigureAwait(false);
        RotateIdentity();
        await RetainFavoritesAsync(null).ConfigureAwait(false);
        var replacement = new LastFmSavedState { HasCredentials = true };
        replacement = await SaveStateAsync(replacement, token).ConfigureAwait(false);
        _saved = replacement;
        var identity = new LastFmIdentity(credentials);
        await store.SaveIdentityAsync(identity, token).ConfigureAwait(false);
        _identity = identity;
        _authToken = null;
        _problem = LastFmProblem.None;
        Publish(true);
    });

    public async Task<Uri> BeginAuthorizationAsync()
    {
        Uri? uri = null;
        await CommandAsync(async token =>
        {
            var credentials = _identity?.Credentials ?? throw new InvalidOperationException("Last.fm credentials are required.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _identityRequests.Token);
            await _network.WaitAsync(linked.Token).ConfigureAwait(false);
            try { _authToken = await api.GetTokenAsync(credentials, linked.Token).ConfigureAwait(false); }
            finally { _network.Release(); }
            _authExpires = DateTimeOffset.UtcNow.AddMinutes(60);
            _problem = LastFmProblem.None;
            Publish();
            uri = new Uri($"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(credentials.ApiKey)}&token={Uri.EscapeDataString(_authToken)}");
        }).ConfigureAwait(false);
        return uri!;
    }

    public Task CompleteAuthorizationAsync() => CommandAsync(async token =>
    {
        if (_authToken is null || DateTimeOffset.UtcNow >= _authExpires)
        {
            _authToken = null;
            _problem = LastFmProblem.AuthorizationExpired;
            Publish();
            return;
        }
        var credentials = _identity!.Credentials;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _identityRequests.Token);
        await _network.WaitAsync(linked.Token).ConfigureAwait(false);
        LastFmAccount account;
        try { account = await api.GetSessionAsync(credentials, _authToken, linked.Token).ConfigureAwait(false); }
        finally { _network.Release(); }
        var identity = new LastFmIdentity(credentials, account);
        // Pending plays belong to both an API application and an account, never to the next login.
        var pending = _saved.Pending.FindAll(play => play.ApiKey == credentials.ApiKey
            && string.Equals(play.Account, account.Name, StringComparison.OrdinalIgnoreCase));
        var known = _saved.Known.FindAll(play => play.ApiKey == credentials.ApiKey
            && string.Equals(play.Account, account.Name, StringComparison.OrdinalIgnoreCase));
        var saved = _saved with { Enabled = true, Pending = pending, Known = known };
        saved = await SaveStateAsync(saved, token).ConfigureAwait(false);
        await store.SaveIdentityAsync(identity, token).ConfigureAwait(false);
        await RetainFavoritesAsync(identity).ConfigureAwait(false);
        RotateIdentity();
        _identity = identity;
        _saved = saved;
        _authToken = null;
        _problem = LastFmProblem.None;
        Publish(true);
        Wake();
    });

    public Task DisconnectAsync()
    {
        CancelRequests();
        return CommandAsync(async token =>
        {
            await DrainNetworkAsync(token).ConfigureAwait(false);
            RotateIdentity();
            await RetainFavoritesAsync(null).ConfigureAwait(false);
            var saved = new LastFmSavedState { HasCredentials = _identity is not null };
            saved = await SaveStateAsync(saved, token).ConfigureAwait(false);
            _saved = saved;
            _identity = _identity is null ? null : new(_identity.Credentials);
            _authToken = null;
            Publish(true);
            await store.SaveIdentityAsync(_identity, token).ConfigureAwait(false);
            _problem = LastFmProblem.None;
            Publish();
        });
    }

    public Task SetEnabledAsync(bool enabled)
    {
        if (!enabled) CancelRequests();
        return CommandAsync(async token =>
        {
            if (_saved.Enabled == enabled) return;
            RotateIdentity();
            var saved = _saved with { Enabled = enabled };
            saved = await SaveStateAsync(saved, token).ConfigureAwait(false);
            _saved = saved;
            Publish(true);
            Wake();
        });
    }

    public Task RetryAsync() => CommandAsync(async _ =>
    {
        // A write may have committed even if the subsequent count/read failed. Retry must discover
        // those durable intents rather than rely on an out-of-date in-memory count until restart.
        if (favorites is not null) _favoritePending = await favorites.ReadPendingCountAsync().ConfigureAwait(false);
        _blocked = false;
        _retryAt = default;
        Publish();
        Wake();
    });

    /// <summary>Explicitly imports existing local history. Queue retry never initiates an import.</summary>
    public Task ImportHistoryAsync(ILastFmHistorySource source, CancellationToken cancellation = default)
        => ImportHistoryAsync(source, false, cancellation);

    public Task ImportHistoryAsync(ILastFmHistorySource source, bool includeOlder, CancellationToken cancellation = default) => CommandAsync(async token =>
    {
        LastFmIdentity identity;
        long revision;
        CancellationToken identityToken;
        var receipts = new HashSet<string>(StringComparer.Ordinal);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CanSend(_revision)) throw new InvalidOperationException("Connect and enable Last.fm before importing history.");
            identity = _identity!;
            revision = _revision;
            identityToken = _identityRequests.Token;
            foreach (var play in _saved.Pending)
                if (BelongsTo(play.ApiKey, play.Account, identity) && play.HistoryKey is not null) receipts.Add(play.HistoryKey);
        }
        finally { _gate.Release(); }
        var commandToken = token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, identityToken);
        token = linked.Token;
        try
        {
            var to = DateTimeOffset.UtcNow;
            var from = to.AddDays(-LastFmHistory.LookbackDays);
            var batch = await LastFmHistory.ReadAsync(source, includeOlder, from, to, receipts, token, store, identity.Account!.Name).ConfigureAwait(false);
            long remoteFrom = from.ToUnixTimeSeconds();
            foreach (var candidate in batch.Candidates) remoteFrom = Math.Min(remoteFrom, candidate.StartedAt);
            var remote = await ReadRemoteHistoryAsync(identity, remoteFrom - 11, to.ToUnixTimeSeconds() + 1,
                batch.Candidates.Count != 0, token).ConfigureAwait(false);
            await CommitHistoryAsync(identity, revision, batch, remote, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (identityToken.IsCancellationRequested && !commandToken.IsCancellationRequested)
        {
            // Account invalidation already published its own status; never replace it with a network error.
        }
    }, cancellation: cancellation, holdState: false);

    private sealed record RemoteHistory(LastFmHistoryIndex Originals, LastFmHistoryIndex All,
        LastFmHistoryIndex Uncertain, HashSet<long> Timestamps);

    private async Task<RemoteHistory> ReadRemoteHistoryAsync(LastFmIdentity identity, long from, long to, bool needed, CancellationToken token)
    {
        var result = new RemoteHistory(new(), new(), new(), []);
        if (!needed) return result;
        int page = 1, pages;
        do
        {
            LastFmRecentPage recent;
            await _network.WaitAsync(token).ConfigureAwait(false);
            try { recent = await api.RecentPlaysAsync(identity, from, to, page, token).ConfigureAwait(false); }
            finally { _network.Release(); }
            pages = recent.TotalPages;
            if (pages is < 0 or > 500) throw new JsonException("Unexpected Last.fm history page count.");
            var aliases = new List<LastFmAlias>(recent.Plays.Count);
            foreach (var play in recent.Plays) aliases.Add(new(LastFmHistory.TrackKey(play.Title, play.Artist), play.StartedAt));
            var matches = await store.ReadAliasesAsync(identity.Account!.Name, aliases, token).ConfigureAwait(false);
            foreach (var alias in aliases)
            {
                result.All.Add(alias.TrackKey, alias.SubmittedAt);
                result.Timestamps.Add(alias.SubmittedAt);
                if (matches.Retimed.Contains(alias)) continue;
                if (matches.Uncertain.Contains(alias)) result.Uncertain.Add(alias.TrackKey, alias.SubmittedAt);
                else result.Originals.Add(alias.TrackKey, alias.SubmittedAt);
            }
            // Yield the shared request slot between pages, including during rate pacing.
            if (page < pages) await Task.Delay(250, token).ConfigureAwait(false);
        } while (++page <= pages);
        return result;
    }

    private async Task CommitHistoryAsync(LastFmIdentity identity, long revision, LastFmHistoryBatch batch,
        RemoteHistory remote, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CanSend(revision)) return;
            if (batch.Candidates.Count == 0)
            {
                _historyImport = new(0, batch.AlreadyPresent, batch.TooOld, batch.Ineligible, batch.Deferred);
                _problem = LastFmProblem.None;
                Publish();
                return;
            }
            var keys = new List<string>(batch.Candidates.Count);
            foreach (var candidate in batch.Candidates) keys.Add(candidate.Key);
            var mappings = await store.ReadMappingsAsync(identity.Account!.Name, keys, token).ConfigureAwait(false);
            var knownPlays = await store.ReadKnownAsync(identity.Account.Name, identity.Credentials.ApiKey, token).ConfigureAwait(false);
            var acknowledged = new LastFmHistoryIndex();
            var queuedPlays = new LastFmHistoryIndex();
            var queuedKeys = new HashSet<string>(StringComparer.Ordinal);
            var importDay = new LastFmImportDay(DateTimeOffset.Now, TimeZoneInfo.Local);
            // Unlink removes the sending queue, but its unresolved reservations still own their
            // assigned seconds, including sources that are absent from this particular import.
            var reservedTimes = await store.ReadReservedTimesAsync(identity.Account.Name,
                DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), token).ConfigureAwait(false);
            foreach (long time in reservedTimes) importDay.Reserve(time);
            foreach (long time in remote.Timestamps) importDay.Reserve(time);
            foreach (var play in knownPlays)
            {
                importDay.Reserve(play.StartedAt);
                if (play.Uncertain) remote.Uncertain.Add(play.TrackKey, play.StartedAt);
                else if (!play.Retimed) acknowledged.Add(play.TrackKey, play.StartedAt);
            }
            int reservedReceipts = await store.CountHistoryAsync(false, token).ConfigureAwait(false);
            foreach (var play in _saved.Pending)
            {
                if (!BelongsTo(play.ApiKey, play.Account, identity)) continue;
                importDay.Reserve(play.StartedAt);
                if (play.HistoryKey is not null) queuedKeys.Add(play.HistoryKey);
                else queuedPlays.Add(LastFmHistory.TrackKey(play.Track.Title, play.Track.Artist), play.StartedAt);
            }
            var pending = new List<LastFmScrobble>(_saved.Pending);
            var addedReceipts = new Dictionary<string, LastFmHistoryReceipt>(StringComparer.Ordinal);
            int queued = 0, retimed = 0, uncertain = 0, alreadyPresent = batch.AlreadyPresent, deferred = batch.Deferred;
            foreach (var candidate in batch.Candidates)
            {
                token.ThrowIfCancellationRequested();
                mappings.TryGetValue(candidate.Key, out var mapping);
                string key = LastFmHistory.TrackKey(candidate.Track.Title, candidate.Track.Artist);
                bool knownAccepted = acknowledged.TryTake(key, candidate.StartedAt, out long acceptedTime);
                bool knownPending = queuedPlays.Take(key, candidate.StartedAt);
                bool knownRemote = remote.Originals.TryTake(key, candidate.StartedAt, out long remoteTime);
                bool mappedRemote = mapping is { SubmittedAt: > 0 } && remote.All.TakeExact(mapping.TrackKey, mapping.SubmittedAt);
                var acknowledgement = Volatile.Read(ref _acknowledged);
                bool mappedAcknowledgement = acknowledgement is { Accepted: true } && acknowledgement.Play.HistoryKey == candidate.Key
                    && string.Equals(acknowledgement.Play.Account, identity.Account.Name, StringComparison.OrdinalIgnoreCase);
                if (mapping?.Accepted == true || queuedKeys.Contains(candidate.Key) || addedReceipts.ContainsKey(candidate.Key)
                    || knownAccepted || knownPending || knownRemote || mappedRemote || mappedAcknowledgement)
                {
                    alreadyPresent++;
                    if (mapping?.Accepted != true && (knownAccepted || knownRemote || mappedRemote || mappedAcknowledgement)
                        && (mapping is not null || reservedReceipts < LastFmHistory.ReceiptLimit))
                    {
                        var receipt = mappedAcknowledgement ? LastFmStore.Receipt(acknowledgement!.Play)
                            : mappedRemote ? new LastFmHistoryReceipt(identity.Account.Name, candidate.Key, mapping!.TrackKey,
                                mapping.OriginalStartedAt, mapping.SubmittedAt, mapping.Retimed)
                            : new LastFmHistoryReceipt(identity.Account.Name, candidate.Key, key, candidate.StartedAt, knownRemote ? remoteTime : acceptedTime);
                        if (addedReceipts.TryAdd(candidate.Key, receipt) && mapping is null) reservedReceipts++;
                    }
                    continue;
                }
                if (mapping is null && remote.Uncertain.Take(key, candidate.StartedAt))
                {
                    // Legacy JSON lacked source-to-upload mappings. Do not convert an ambiguous match
                    // into a permanent success receipt, or submit it blindly as a second play.
                    uncertain++;
                    deferred++;
                    continue;
                }
                if (pending.Count >= 10000 || (mapping is null && reservedReceipts >= LastFmHistory.ReceiptLimit)) { deferred++; continue; }
                long timestamp = candidate.StartedAt;
                var track = candidate.Track;
                bool useImportDay = candidate.Retimed;
                if (mapping is { SubmittedAt: > 0, Track: not null } && mapping.SubmittedAt >= LastFmHistory.OldestTimestamp)
                {
                    // A canceled/unlinked reservation may already have reached Last.fm. Reuse its
                    // exact payload and timestamp until the original attempt can be reconciled.
                    timestamp = mapping.SubmittedAt;
                    track = mapping.Track;
                    useImportDay = mapping.Retimed;
                }
                else if (useImportDay && !importDay.TryAllocate(out timestamp)) { deferred++; continue; }
                importDay.Reserve(timestamp);
                pending.Add(new(Guid.NewGuid(), identity.Credentials.ApiKey, identity.Account.Name, track, timestamp,
                    candidate.Key, candidate.StartedAt, useImportDay));
                queuedKeys.Add(candidate.Key);
                if (mapping is null) reservedReceipts++;
                queued++;
                if (useImportDay) retimed++;
            }
            if (addedReceipts.Count != 0)
                pending.RemoveAll(play => play.HistoryKey is not null && addedReceipts.ContainsKey(play.HistoryKey)
                    && string.Equals(play.Account, identity.Account.Name, StringComparison.OrdinalIgnoreCase));
            token.ThrowIfCancellationRequested();
            if (queued != 0 || addedReceipts.Count != 0)
                _saved = await SaveStateAsync(_saved with { Pending = pending }, token, addedReceipts.Values).ConfigureAwait(false);
            _historyImport = new(queued, alreadyPresent, batch.TooOld, batch.Ineligible, deferred, retimed, uncertain);
            _problem = LastFmProblem.None;
            _blocked = false;
            _retryAt = default;
            Publish();
            Wake();
        }
        finally { _gate.Release(); }
    }

    private async Task DrainNetworkAsync(CancellationToken token)
    {
        await _network.WaitAsync(token).ConfigureAwait(false);
        _network.Release();
    }

    private async Task<LastFmSavedState> SaveStateAsync(LastFmSavedState state, CancellationToken token,
        IReadOnlyCollection<LastFmHistoryReceipt>? receipts = null, LastFmAcknowledgement? outcome = null)
    {
        var inMemory = Volatile.Read(ref _acknowledged);
        var acknowledgement = inMemory is not null && (outcome is null || outcome.Play.Id == inMemory.Play.Id) ? inMemory : outcome;
        if (acknowledgement is not null)
            state = state with { Pending = state.Pending.FindAll(play => play.Id != acknowledgement.Play.Id) };
        await store.CommitAsync(state, acknowledgement, receipts ?? Array.Empty<LastFmHistoryReceipt>(), token).ConfigureAwait(false);
        if (inMemory is not null && acknowledgement?.Play.Id == inMemory.Play.Id)
            Interlocked.CompareExchange(ref _acknowledged, null, inMemory);
        return state with { Version = 2, Known = [], HistoryReceipts = [] };
    }

    private static bool BelongsTo(string apiKey, string account, LastFmIdentity identity) => apiKey == identity.Credentials.ApiKey
        && string.Equals(account, identity.Account!.Name, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> EnqueueAsync(LastFmTrack track, long startedAt, Guid id, long revision)
    {
        if (_stopped) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!CanSend(revision)) return false;
            if (_saved.Pending.Exists(play => play.Id == id)) return true;
            if (_saved.Pending.Count >= 10000)
            {
                _problem = LastFmProblem.QueueFull;
                Publish();
                return false;
            }
            var pending = new List<LastFmScrobble>(_saved.Pending)
            {
                new(id, _identity!.Credentials.ApiKey, _identity.Account!.Name, track, startedAt)
            };
            var saved = _saved with { Pending = pending };
            // Commit before acknowledging qualification or attempting any network request.
            saved = await SaveStateAsync(saved, CancellationToken.None).ConfigureAwait(false);
            _saved = saved;
            Publish();
            Wake();
            return true;
        }
        catch
        {
            _problem = LastFmProblem.Storage;
            Publish();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task NowPlayingAsync(LastFmTrack track, long revision, CancellationToken selection)
    {
        if (_stopped) return;
        var request = await GetRequestAsync(revision).ConfigureAwait(false);
        if (request is null) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, request.Value.Token, selection);
        bool entered = false;
        Exception? failure = null;
        try
        {
            await _network.WaitAsync(linked.Token).ConfigureAwait(false);
            entered = true;
            await api.NowPlayingAsync(request.Value.Identity, track, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally { if (entered) _network.Release(); }
        if (failure is not null) await FailedAsync(failure, revision).ConfigureAwait(false);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                while (!_stop.IsCancellationRequested)
                {
                    LastFmScrobble? play;
                    LastFmFavoriteIntent? favorite = null;
                    bool hasFavorite = false;
                    Exception? favoriteReadError = null;
                    long revision;
                    DateTimeOffset retryAt;
                    await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        revision = _revision;
                        play = CanSend(revision) && !_blocked && _saved.Pending.Count > 0 ? _saved.Pending[0] : null;
                        hasFavorite = CanSend(revision) && !_blocked && _favoritePending > 0 && (play is null || _favoriteTurn);
                        if (hasFavorite && _retryAt <= DateTimeOffset.UtcNow)
                        {
                            try { favorite = await favorites!.PeekAsync(_identity!.Credentials.ApiKey, _identity.Account!.Name).ConfigureAwait(false); }
                            catch (Exception ex) { favoriteReadError = ex; }
                        }
                        retryAt = _retryAt;
                    }
                    finally { _gate.Release(); }
                    if (favoriteReadError is not null)
                    {
                        _favoriteTurn = false;
                        await FailedAsync(favoriteReadError, revision).ConfigureAwait(false);
                        continue;
                    }
                    if (play is null && favorite is null && !hasFavorite) break;
                    var delay = retryAt - DateTimeOffset.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false);
                        continue;
                    }
                    if (favorite is not null)
                    {
                        _favoriteTurn = false;
                        await SendFavoriteAsync(favorite, revision).ConfigureAwait(false);
                        continue;
                    }
                    if (play is null) break;
                    _favoriteTurn = true;
                    // A known server acknowledgement survives a failed local queue rewrite.
                    // Retry the commit, not the already accepted HTTP submission, in this process.
                    var acknowledgement = Volatile.Read(ref _acknowledged);
                    if (play!.Id == acknowledgement?.Play.Id)
                    {
                        try { await RemoveAsync(play.Id, revision, acknowledgement.Accepted).ConfigureAwait(false); }
                        catch (Exception ex) { await FailedAsync(ex, revision).ConfigureAwait(false); }
                        continue;
                    }
                    var request = await GetRequestAsync(revision).ConfigureAwait(false);
                    if (request is null) break;
                    if (play.ApiKey != request.Value.Identity.Credentials.ApiKey
                        || !string.Equals(play.Account, request.Value.Identity.Account!.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        await RemoveAsync(play.Id, revision, false).ConfigureAwait(false);
                        continue;
                    }
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, request.Value.Token);
                    bool entered = false;
                    bool? accepted = null;
                    Exception? failure = null;
                    try
                    {
                        await _network.WaitAsync(linked.Token).ConfigureAwait(false);
                        entered = true;
                        accepted = await api.ScrobbleAsync(request.Value.Identity, play, linked.Token).ConfigureAwait(false);
                        Volatile.Write(ref _acknowledged, new(play, accepted.Value));
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                    catch (LastFmApiException ex) when (ex.Code is 6 or 7)
                    {
                        accepted = false;
                        Volatile.Write(ref _acknowledged, new(play, false));
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { if (entered) _network.Release(); }
                    if (accepted is bool result)
                    {
                        try { await RemoveAsync(play.Id, revision, result).ConfigureAwait(false); }
                        catch (Exception ex) { failure = ex; }
                    }
                    if (failure is not null) await FailedAsync(failure, revision).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task RemoveAsync(Guid id, long revision, bool accepted)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (revision != _revision) return;
            var removed = _saved.Pending.Find(play => play.Id == id);
            if (removed is null) return;
            var saved = _saved with { Pending = _saved.Pending.FindAll(play => play.Id != id) };
            _saved = await SaveStateAsync(saved, CancellationToken.None, outcome: new(removed, accepted)).ConfigureAwait(false);
            _failures = 0;
            _retryAt = default;
            _problem = accepted ? LastFmProblem.None : LastFmProblem.Rejected;
            Publish();
        }
        finally { _gate.Release(); }
    }

    private async Task FailedAsync(Exception error, long revision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped || revision != _revision) return;
            _problem = error is JsonException or System.Collections.Generic.KeyNotFoundException
                ? LastFmProblem.Network : ProblemFor(error);
            _retryAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(900, _retryUnitSeconds * Math.Pow(2, Math.Min(++_failures, 5))));
            if (error is LastFmApiException { Code: 9 })
            {
                RotateIdentity();
                _identity = _identity! with { Account = new(_identity!.Account!.Name, "") };
                Publish(true);
                try { await store.SaveIdentityAsync(_identity, _stop.Token).ConfigureAwait(false); }
                catch (Exception) when (!_stop.IsCancellationRequested) { _problem = LastFmProblem.Storage; }
            }
            else if (error is LastFmApiException { Retryable: false }) _blocked = true;
            Publish();
        }
        finally { _gate.Release(); }
    }

    private async Task<(LastFmIdentity Identity, CancellationToken Token)?> GetRequestAsync(long revision)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return CanSend(revision) ? (_identity!, _identityRequests.Token) : null; }
        finally { _gate.Release(); }
    }

    private bool CanSend(long revision) => !_stopped && _ready && revision == _revision && _saved.Enabled
        && !string.IsNullOrEmpty(_identity?.Account?.SessionKey);

    private async Task CommandAsync(Func<CancellationToken, Task> action, bool requireReady = true, CancellationToken cancellation = default, bool holdState = true)
    {
        if (_stopped) throw new OperationCanceledException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellation);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
            bool gateHeld = true;
            try
            {
                if (requireReady && !_ready) throw new IOException("Last.fm storage is not ready.");
                if (!holdState)
                {
                    _gate.Release();
                    gateHeld = false;
                }
                await action(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested || _identityRequests.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (!gateHeld)
                {
                    await _gate.WaitAsync().ConfigureAwait(false);
                    gateHeld = true;
                }
                if (ex is LastFmApiException { Code: 4 or 15 }) _authToken = null;
                _problem = requireReady && ex is (JsonException or System.Collections.Generic.KeyNotFoundException)
                    ? LastFmProblem.Network : ProblemFor(ex);
                Publish();
                throw;
            }
            finally { if (gateHeld) _gate.Release(); }
        }
        finally { _commands.Release(); }
    }

    private static LastFmProblem ProblemFor(Exception error) => error switch
    {
        LastFmApiException { Code: 9 } => LastFmProblem.SessionExpired,
        LastFmApiException { Code: 14 } => LastFmProblem.AuthorizationPending,
        LastFmApiException { Code: 4 or 15 } => LastFmProblem.AuthorizationExpired,
        LastFmApiException { Retryable: false } or ArgumentException => LastFmProblem.Credentials,
        LastFmApiException or HttpRequestException or OperationCanceledException => LastFmProblem.Network,
        _ => LastFmProblem.Storage
    };

    private static bool IsHexKey(string key)
    {
        if (key.Length != 32) return false;
        foreach (char character in key) if (!Uri.IsHexDigit(character)) return false;
        return true;
    }

    private void Publish(bool identityChanged = false)
    {
        if (identityChanged) _revision++;
        var status = new LastFmStatus(_identity?.Credentials.ApiKey ?? "", !string.IsNullOrEmpty(_identity?.Credentials.SharedSecret),
            _identity?.Account?.Name ?? "", !string.IsNullOrEmpty(_identity?.Account?.SessionKey), _saved.Enabled,
            _saved.Pending.Count, _authToken is not null, _problem, _revision, _ready, _historyImport, _favoritePending, _favoriteImport);
        Volatile.Write(ref _status, status);
        Changed?.Invoke(status);
    }

    private void CancelRequests()
    {
        // Cancellation must not wait behind a slow HTTP response holding a command gate.
        try { _identityRequests.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void CancelNetworkRequests() => CancelRequests();

    private void RotateIdentity()
    {
        _historyImport = null;
        _favoriteImport = null;
        CancelRequests();
        _identityRequests.Dispose();
        _identityRequests = new();
        _blocked = false;
        _failures = 0;
        _retryAt = default;
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public Task StopAsync()
    {
        lock (_stop) return _stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        _stopped = true;
        _stop.Cancel();
        CancelRequests();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        // Wait for configuration, queue commits and in-flight Now Playing requests before releasing HTTP.
        await _commands.WaitAsync().ConfigureAwait(false);
        _commands.Release();
        await _network.WaitAsync().ConfigureAwait(false);
        _network.Release();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _acknowledged) is not null)
                _saved = await SaveStateAsync(_saved, CancellationToken.None).ConfigureAwait(false);
            if (_favoriteAcknowledged is { } favorite)
                await CompleteFavoriteAsync(favorite.Intent, favorite.Accepted).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            Changed = null;
            await store.CloseAsync().ConfigureAwait(false);
        }
    }
}
