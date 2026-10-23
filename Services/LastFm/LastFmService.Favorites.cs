using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed partial class LastFmService
{
    private int _favoritePending;
    private bool _favoriteTurn = true;
    private LastFmFavoriteImportResult? _favoriteImport;
    private sealed class FavoriteImportSession(long revision)
    {
        public long Revision { get; } = revision;
        public HashSet<string> ChangedTracks { get; } = new(StringComparer.Ordinal);
    }
    private FavoriteImportSession? _favoriteImportSession;
    private sealed record FavoriteAcknowledgement(LastFmFavoriteIntent Intent, bool Accepted);
    private FavoriteAcknowledgement? _favoriteAcknowledged;
    public bool SupportsFavorites => favorites is not null;

    private async Task RetainFavoritesAsync(LastFmIdentity? identity)
    {
        if (favorites is null) return;
        _favoritePending = await favorites.RetainAsync(identity?.Credentials.ApiKey, identity?.Account?.Name).ConfigureAwait(false);
        _favoriteAcknowledged = null;
    }

    public async Task SaveFavoriteAsync(LastFmFavoriteChange change)
    {
        if (favorites is null) throw new InvalidOperationException("The library favorite store is not configured.");
        // Capture the owner before waiting. A click made for A must never become an operation for B.
        var owner = Status;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool sameOwner = !_stopped && _ready && owner.ApiKey == _identity?.Credentials.ApiKey
                && string.Equals(owner.UserName, _identity?.Account?.Name, StringComparison.OrdinalIgnoreCase);
            _favoritePending = await favorites.SaveLocalAsync(change, sameOwner ? owner.ApiKey : null,
                sameOwner ? owner.UserName : null).ConfigureAwait(false);
            if (sameOwner && _favoriteImportSession is { } importing && importing.Revision == _revision
                && favorites.EligibleTrackKey(change.Track) is { } key)
                importing.ChangedTracks.Add(key);
            Publish();
            Wake();
        }
        catch
        {
            _problem = LastFmProblem.Storage;
            Publish();
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveFavoriteOrderAsync(IReadOnlyList<LastFmFavoriteOrder> orders)
    {
        if (favorites is null) throw new InvalidOperationException("The library favorite store is not configured.");
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await favorites.SaveOrderAsync(orders).ConfigureAwait(false); }
        catch
        {
            _problem = LastFmProblem.Storage;
            Publish();
            throw;
        }
        finally { _gate.Release(); }
    }

    public Task ImportFavoritesAsync(CancellationToken cancellation = default) => CommandAsync(async token =>
    {
        LastFmIdentity identity;
        CancellationToken identityToken;
        FavoriteImportSession importing;
        int endId;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!CanSend(_revision) || favorites is null) throw new InvalidOperationException("Connect and enable Last.fm before reporting favorites.");
            identity = _identity!;
            identityToken = _identityRequests.Token;
            endId = await favorites.ReadImportEndAsync().ConfigureAwait(false);
            importing = new(_revision);
            _favoriteImportSession = importing;
            _favoriteImport = new(0, 0);
            Publish();
        }
        finally { _gate.Release(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, identityToken);
        int afterId = 0, queued = 0, ineligible = 0;
        try
        {
            while (true)
            {
                LastFmFavoriteImportBatch batch;
                await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    if (!CanSend(importing.Revision)) return;
                    batch = await favorites!.ImportBatchAsync(identity.Credentials.ApiKey, identity.Account!.Name,
                        afterId, endId, importing.ChangedTracks, linked.Token).ConfigureAwait(false);
                    queued += batch.Queued;
                    ineligible += batch.Ineligible;
                    // All count-changing service operations own this gate. Replacements do not
                    // grow the queue; apply the committed insert delta instead of recounting the
                    // increasingly large table after every page.
                    _favoritePending += batch.Added;
                    _favoriteImport = new(queued, ineligible);
                    Publish();
                    Wake();
                }
                finally { _gate.Release(); }
                if (batch.Scanned < LastFmFavoriteStore.ImportBatchSize || batch.LastId >= endId) break;
                afterId = batch.LastId;
                // Each committed page releases both the shared library connection and service gate.
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_revision == importing.Revision)
                {
                    _favoriteImport = new(queued, ineligible, Canceled: true);
                    Publish();
                }
            }
            finally { _gate.Release(); }
            if (!identityToken.IsCancellationRequested || token.IsCancellationRequested) throw;
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { if (ReferenceEquals(_favoriteImportSession, importing)) _favoriteImportSession = null; }
            finally { _gate.Release(); }
        }
    }, cancellation: cancellation, holdState: false);

    private async Task SendFavoriteAsync(LastFmFavoriteIntent intent, long revision)
    {
        try
        {
            var acknowledgement = _favoriteAcknowledged;
            if (acknowledgement?.Intent.Version == intent.Version)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { if (revision == _revision) await CompleteFavoriteAsync(intent, acknowledgement.Accepted).ConfigureAwait(false); }
                finally { _gate.Release(); }
                return;
            }
            var request = await GetRequestAsync(revision).ConfigureAwait(false);
            if (request is null) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, request.Value.Token);
            bool? accepted = null;
            await _network.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                // Recheck the durable version after waiting for Now Playing/history HTTP requests.
                // Never acquire the service gate while holding the network slot: unlink drains it.
                if (!await favorites!.MarkAttemptedAsync(intent).ConfigureAwait(false)) return;
                linked.Token.ThrowIfCancellationRequested();
                bool confirmed = false;
                if (intent.Attempted)
                {
                    try { confirmed = await api.IsLovedAsync(request.Value.Identity, intent.Track, linked.Token).ConfigureAwait(false) == intent.Loved; }
                    catch (LastFmApiException ex) when (ex.Code == 6) { /* Track not found: attempt the explicit write. */ }
                }
                if (!confirmed) await api.SetLovedAsync(request.Value.Identity, intent.Track, intent.Loved, linked.Token).ConfigureAwait(false);
                accepted = true;
                _favoriteAcknowledged = new(intent, true);
            }
            catch (LastFmApiException ex) when (ex.Code is 6 or 7)
            {
                accepted = false;
                _favoriteAcknowledged = new(intent, false);
            }
            finally { _network.Release(); }
            if (accepted is bool result)
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try { if (revision == _revision) await CompleteFavoriteAsync(intent, result).ConfigureAwait(false); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested || revision != Status.Revision) { }
        catch (Exception ex) { await FailedAsync(ex, revision).ConfigureAwait(false); }
    }

    // Caller owns _gate. Deleting by version cannot acknowledge a newer love/unlove for the same track.
    private async Task CompleteFavoriteAsync(LastFmFavoriteIntent intent, bool accepted)
    {
        _favoritePending = await favorites!.CompleteAsync(intent).ConfigureAwait(false);
        if (_favoriteAcknowledged?.Intent.Version == intent.Version) _favoriteAcknowledged = null;
        _failures = 0;
        _retryAt = default;
        _problem = accepted ? LastFmProblem.None : LastFmProblem.Rejected;
        Publish();
    }
}
