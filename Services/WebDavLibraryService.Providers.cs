using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private readonly CatalogProviders _catalog = new();

    public static string GetResourceIdentity(RemoteTrack track) => string.IsNullOrEmpty(track.ResourcePath)
        ? $"webdav://{track.SourceId}{track.Href}"
        : track.ResourcePath;

    /// <summary>建立播放器的只读入口。源站凭据延迟到实际发起上游请求时取得。</summary>
    public Func<long?, long?, bool, CancellationToken, Task<WebDavResponse>> CreateAudioOpener(WebDavSource source, RemoteTrack track)
    {
        if (source.Kind == RemoteSourceKind.WebDav)
        {
            var connection = new Lazy<WebDavConnection>(() => Connect(source));
            return (start, end, foreground, token) => transport.OpenAsync(connection.Value, track.Href, start, end, foreground, token);
        }
        var provider = _catalog.Get(source.Kind);
        return async (start, end, foreground, token) =>
        {
            var session = await ConnectCatalogAsync(source, provider, token).ConfigureAwait(false);
            try
            {
                return await provider.OpenAudioAsync(session, track.Href, start, end, foreground, token).ConfigureAwait(false);
            }
            catch (WebDavException ex) when (ex.Code == "AuthenticationFailed")
            {
                _catalog.Forget(source);
                throw;
            }
        };
    }

    public Task TestSourceAsync(WebDavSource source, string? password, CancellationToken token) =>
        RunSourceOperationAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, token);
            string stage = "NormalizeAddress";
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var root = WebDavTransport.NormalizeRoot(source.BaseUri);

                stage = "ReadCredentials";
                WebDavSource? previous = source.Id == 0 ? null : await database.GetRemoteSourceAsync(source.Id).ConfigureAwait(false);
                if (source.Id != 0) ValidateReplacement(source, previous, root.AbsoluteUri);
                if (password is null && previous is not null && (previous.BaseUri != root.AbsoluteUri || previous.UserName != source.UserName))
                    throw new WebDavException("AuthenticationRequired");
                string secret = password ?? (previous is null ? "" : ReadSourceSecret(previous));

                if (source.Kind == RemoteSourceKind.WebDav)
                {
                    stage = "ProbeDirectory";
                    var trust = string.IsNullOrEmpty(source.TrustedCertificateSha256) ? null :
                        new WebDavCertificateTrust(source.TrustedCertificateOrigin, source.TrustedCertificateSha256);
                    await transport.ProbeAsync(new(root, source.UserName, secret, trust), linked.Token).ConfigureAwait(false);
                    return;
                }

                stage = "Authenticate";
                if (string.IsNullOrWhiteSpace(source.UserName)) throw new WebDavException("AuthenticationRequired");
                var provider = _catalog.Get(source.Kind);
                var session = await provider.AuthenticateAsync(root, source.UserName, secret, linked.Token,
                    previous?.CredentialKey ?? source.CredentialKey).ConfigureAwait(false);
                stage = "ProbeCatalog";
                await provider.ProbeAsync(session, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !linked.IsCancellationRequested)
            {
                var status = ex switch
                {
                    WebDavException failure => failure.Status,
                    HttpRequestException failure => failure.StatusCode,
                    _ => null
                };
                // 仅记录类型与状态码，避免异常文本包含服务器地址、查询参数或凭据。
                logger.LogWarning("Remote source connection test failed: {Protocol} {Stage}, exception {ExceptionType} ({ExceptionHResult:X8}), inner {InnerExceptionType} ({InnerExceptionHResult:X8}), HTTP {HttpStatus}",
                    source.Kind, stage, ex.GetType().Name, ex.HResult, ex.InnerException?.GetType().Name, ex.InnerException?.HResult, (int?)status);
                throw;
            }
        });

    private Task<CatalogSession> ConnectCatalogAsync(WebDavSource source, IRemoteMediaProvider provider, CancellationToken token) =>
        _catalog.ConnectAsync(source, provider, token);

    private async Task<bool> IsCurrentSourceAsync(WebDavSource source)
    {
        var current = await database.GetRemoteSourceAsync(source.Id).ConfigureAwait(false);
        return current is { Enabled: true } && current.Kind == source.Kind && current.ConfigurationRevision == source.ConfigurationRevision;
    }

    private void OnSourceConfigurationChanged(WebDavSource previous, bool enabled)
    {
        _availability.Invalidate(previous.Id, offline: !enabled);
        lock (_gate)
            if (_probes.Remove(previous.Id, out var probe) && !probe.Work.IsCompleted) probe.Cancel.Cancel();
        _catalog.Forget(previous);
        PublishAvailability(previous.Id);
    }

    private async Task ProbeCatalogAsync(WebDavSource source, CancellationToken token)
    {
        var provider = _catalog.Get(source.Kind);
        var session = await ConnectCatalogAsync(source, provider, token).ConfigureAwait(false);
        try { await provider.ProbeAsync(session, token).ConfigureAwait(false); }
        catch (WebDavException ex) when (ex.Code == "AuthenticationFailed")
        {
            _catalog.Forget(source);
            throw;
        }
    }

    private async Task ScanCatalogAsync(WebDavSource source, CancellationToken token)
    {
        int found = 0;
        int changes = 0;
        string phase = "Completed";
        string? error = null;
        try
        {
            if (!await IsCurrentSourceAsync(source).ConfigureAwait(false)) return;
            var provider = _catalog.Get(source.Kind);
            var session = await ConnectCatalogAsync(source, provider, token).ConfigureAwait(false);
            string run = Guid.NewGuid().ToString("N");
            var batch = new List<CatalogTrack>(64);
            Publish(new(source.Id, "Scanning", found, found));
            await foreach (var entry in provider.EnumerateAsync(session, token).ConfigureAwait(false))
            {
                batch.Add(entry);
                found++;
                if (batch.Count == 64) await FlushAsync().ConfigureAwait(false);
            }
            if (batch.Count != 0) await FlushAsync().ConfigureAwait(false);
            changes += await database.CompleteCatalogSyncAsync(source, run, token).ConfigureAwait(false);

            async Task FlushAsync()
            {
                var changed = await database.CommitCatalogTracksAsync(source, run, batch, token).ConfigureAwait(false);
                changes += changed.Count;
                await PublishBatchAsync(changed).ConfigureAwait(false);
                batch.Clear();
                Publish(new(source.Id, "Scanning", found, found));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            phase = "Cancelled";
        }
        catch (Exception ex)
        {
            error = ex is WebDavException failure ? failure.Code : "ConnectionFailed";
            phase = "Failed";
            if (error == "AuthenticationFailed") _catalog.Forget(source);
            logger.LogWarning("Music source {SourceId} sync failed: {Error}", source.Id, error);
        }
        if (changes != 0 && _library is not null && !_stop.IsCancellationRequested)
        {
            try { await _library.RefreshSongsSourceAsync(_stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch { logger.LogWarning("Music source {SourceId} index refresh failed", source.Id); }
        }
        Publish(new(source.Id, phase, found, found, error));
    }

    private async Task<byte[]> ReadCatalogCoverAsync(WebDavSource source, RemoteTrack track, CancellationToken token)
    {
        if (string.IsNullOrEmpty(track.CoverId)) return [];
        var provider = _catalog.Get(source.Kind);
        var session = await ConnectCatalogAsync(source, provider, token).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            await using var response = await provider.OpenCoverAsync(session, track.CoverId, deadline.Token).ConfigureAwait(false);
            using var body = await RemoteMediaHttp.ReadBodyAsync(response, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
            return body.ToArray();
        }
        catch (WebDavException ex) when (ex.Status == HttpStatusCode.NotFound || ex.Code == "ResourceMissing") { return []; }
        catch (WebDavException ex) when (ex.Code == "AuthenticationFailed")
        {
            _catalog.Forget(source);
            throw;
        }
    }

    private sealed class CatalogProviders : IDisposable
    {
        private readonly RemoteMediaHttp _http = new();
        private readonly Dictionary<RemoteSourceKind, IRemoteMediaProvider> _providers;
        private readonly ConcurrentDictionary<(int Id, int Revision), SessionCache> _sessions = new();

        public CatalogProviders()
        {
            IRemoteMediaProvider jellyfin = new JellyfinProvider(_http);
            IRemoteMediaProvider navidrome = new NavidromeProvider(_http);
            _providers = new() { [jellyfin.Kind] = jellyfin, [navidrome.Kind] = navidrome };
        }

        public IRemoteMediaProvider Get(RemoteSourceKind kind) => _providers.TryGetValue(kind, out var provider)
            ? provider : throw new WebDavException("SourceUnavailable");

        public Task<CatalogSession> ConnectAsync(WebDavSource source, IRemoteMediaProvider provider, CancellationToken token) =>
            _sessions.GetOrAdd((source.Id, source.ConfigurationRevision), static _ => new()).ConnectAsync(source, provider, token);

        public void Forget(WebDavSource source) => _sessions.TryRemove((source.Id, source.ConfigurationRevision), out _);

        public void Dispose()
        {
            _sessions.Clear();
            _http.Dispose();
        }

        private sealed class SessionCache
        {
            private readonly SemaphoreSlim _gate = new(1, 1);
            private CatalogSession? _session;

            public async Task<CatalogSession> ConnectAsync(WebDavSource source, IRemoteMediaProvider provider, CancellationToken token)
            {
                var session = Volatile.Read(ref _session);
                if (session is not null) return session;
                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    session = _session;
                    if (session is not null) return session;
                    token.ThrowIfCancellationRequested();
                    string secret = ReadSourceSecret(source);
                    session = await provider.AuthenticateAsync(new Uri(source.BaseUri), source.UserName, secret, token,
                        source.CredentialKey).ConfigureAwait(false);
                    Volatile.Write(ref _session, session);
                    return session;
                }
                finally { _gate.Release(); }
            }
        }
    }
}
