using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.LastFm;

public sealed partial class LastFmService
{
    private int _favoritePending;
    private bool _favoriteTurn = true;
    private LastFmFavoriteImportResult? _favoriteImport;
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

    public Task ImportFavoritesAsync() => CommandAsync(async _ =>
    {
        if (!CanSend(_revision) || favorites is null) throw new InvalidOperationException("Connect and enable Last.fm before reporting favorites.");
        (_favoritePending, _favoriteImport) = await favorites.ImportAsync(_identity!.Credentials.ApiKey, _identity.Account!.Name).ConfigureAwait(false);
        Publish();
        Wake();
    });

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
