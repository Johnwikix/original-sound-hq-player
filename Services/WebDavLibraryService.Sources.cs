using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private readonly HashSet<Task> _sourceSaves = [];
    private readonly HashSet<int> _changingSources = [];
    private readonly SemaphoreSlim _sourceWrites = new(1, 1);
    private bool CanPublishCommittedSource => !_stop.IsCancellationRequested && _library?.CanPublishState != false;

    public Task SaveSourceAsync(WebDavSource source, string? password) =>
        RunSourceOperationAsync(() => SaveSourceCoreAsync(source, password, scanAfterSave: false));

    /// <summary>The save result includes scan registration. The scan itself belongs to the source service.</summary>
    internal Task SaveAndScanSourceAsync(WebDavSource source, string? password) =>
        RunSourceOperationAsync(() => SaveSourceCoreAsync(source, password, scanAfterSave: true));

    private Task RunSourceOperationAsync(Func<Task> operation)
    {
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            // PasswordVault 是同步 WinRT 调用。凭据开始写入后要完成事务或补偿，退出等待真实任务收尾。
            return StartSourceOperationLocked(operation);
        }
    }

    private Task StartSourceOperationLocked(Func<Task> operation)
    {
        var work = Task.Run(operation);
        _sourceSaves.Add(work);
        _ = ObserveSourceSaveAsync(work);
        return work;
    }

    private async Task SaveSourceCoreAsync(WebDavSource source, string? password, bool scanAfterSave)
    {
        await _sourceWrites.WaitAsync(_stop.Token).ConfigureAwait(false);
        bool isNew = source.Id == 0;
        bool refreshScope = !isNew && source.Kind == RemoteSourceKind.WebDav;
        try
        {
            source.BaseUri = WebDavTransport.NormalizeRoot(source.BaseUri).AbsoluteUri;
            if (string.IsNullOrWhiteSpace(source.Name)) throw new WebDavException("NameRequired");
            if (!Enum.IsDefined(source.Kind)) throw new WebDavException("SourceUnavailable");
            WebDavSource? previous = null;
            foreach (var existing in await database.GetRemoteSourcesAsync().ConfigureAwait(false))
            {
                if (existing.Id == source.Id) previous = existing;
                else if (existing.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                    throw new WebDavException("NameExists");
            }
            if (!isNew)
            {
                ValidateReplacement(source, previous, source.BaseUri);
                lock (_gate) _changingSources.Add(source.Id);
                await CancelScanAsync(source.Id).ConfigureAwait(false);
                source.CredentialKey = previous!.CredentialKey;
                source.ConfigurationRevision = checked(previous.ConfigurationRevision + 1);
                source.LastScanUtc = previous.LastScanUtc;
            }
            if (source.Kind != RemoteSourceKind.WebDav && string.IsNullOrWhiteSpace(source.UserName))
                throw new WebDavException("AuthenticationRequired");
            bool sameIdentity = previous is not null && previous.BaseUri == source.BaseUri && previous.UserName == source.UserName;
            if (source.UserName.Length != 0 && password is null && !sameIdentity)
                throw new WebDavException("AuthenticationRequired");
            bool updateSecret = source.UserName.Length != 0 && password is not null &&
                (!sameIdentity || TryReadSourceSecret(previous!) != password);
            if (updateSecret && previous is not null) source.CredentialKey = Guid.NewGuid().ToString("N");
            bool provisionalCredential = false;
            try
            {
                if (updateSecret)
                {
                    WriteSourceSecret(source, password!);
                    provisionalCredential = true;
                }
                await database.SaveWebDavSourceAsync(source).ConfigureAwait(false);
            }
            catch
            {
                if (provisionalCredential) RemoveSourceSecret(source);
                if (isNew)
                {
                    // SQLite can assign the ID before a later step in the transaction fails.
                    source.Id = 0;
                }
                else if (previous is not null)
                {
                    source.CredentialKey = previous.CredentialKey;
                    source.ConfigurationRevision = previous.ConfigurationRevision;
                }
                throw;
            }
            if (previous is not null)
            {
                if (source.UserName.Length == 0 || updateSecret) RemoveSourceSecret(previous);
                try { OnSourceConfigurationChanged(previous, source.Enabled); }
                catch (Exception ex)
                {
                    logger.LogWarning("Committed source {SourceId} could not publish availability: {ErrorType}", source.Id, ex.GetType().Name);
                }
            }
            if (scanAfterSave && source.Enabled)
            {
                lock (_gate)
                {
                    // Still hold the source-write gate so an edit or removal cannot get ahead of this scan registration.
                    _changingSources.Remove(source.Id);
                    if (CanPublishCommittedSource)
                    {
                        _ = ScanAsync(source, refreshScope);
                        _ = StartSourceOperationLocked(() => PublishSavedSourceAsync(source));
                    }
                }
                return;
            }
            // Save-only edits must finish publishing the new scope before a later scan can append to it.
            if (refreshScope) await RefreshSavedScopeAsync(source.Id, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!isNew) lock (_gate) _changingSources.Remove(source.Id);
            _sourceWrites.Release();
        }
        await PublishSavedSourceAsync(source).ConfigureAwait(false);
    }

    private async Task RefreshSavedScopeAsync(int sourceId, CancellationToken token)
    {
        if (_library is null || !_library.CanPublishState || token.IsCancellationRequested) return;
        try { await _library.RefreshSongsSourceAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogWarning("Committed source {SourceId} scope refresh failed: {ErrorType}", sourceId, ex.GetType().Name);
        }
    }

    private async Task PublishSavedSourceAsync(WebDavSource source)
    {
        if (!CanPublishCommittedSource) return;
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (!CanPublishCommittedSource) return;
                if (source.Enabled) _ = ProbeAsync(source);
                SourcesChanged?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Committed source {SourceId} publication failed: {ErrorType}", source.Id, ex.GetType().Name);
        }
    }

    private static void ValidateReplacement(WebDavSource source, WebDavSource? previous, string root)
    {
        if (previous is null || previous.Kind != source.Kind) throw new WebDavException("SourceUnavailable");
        // A catalog ID is only stable within the same server and account.
        if (source.Kind != RemoteSourceKind.WebDav && (previous.BaseUri != root || previous.UserName != source.UserName))
            throw new WebDavException("InvalidAddress");
    }

    private static string VaultResource(WebDavSource source) => source.Kind == RemoteSourceKind.WebDav
        ? "OriginalSoundPlayer.WebDav" : "OriginalSoundPlayer.MediaCatalog";

    private static string? TryReadSourceSecret(WebDavSource source)
    {
        if (source.UserName.Length == 0) return "";
        try
        {
            var credential = new PasswordVault().Retrieve(VaultResource(source), source.CredentialKey);
            credential.RetrievePassword();
            string password = credential.Password;
            return source.Kind == RemoteSourceKind.WebDav ? password :
                password.StartsWith("v1:", StringComparison.Ordinal) ? password[3..] : null;
        }
        catch { return null; }
    }

    private static string ReadSourceSecret(WebDavSource source) =>
        TryReadSourceSecret(source) ?? throw new WebDavException("AuthenticationRequired");

    private static void WriteSourceSecret(WebDavSource source, string password)
    {
        string value = source.Kind == RemoteSourceKind.WebDav ? password : "v1:" + password;
        new PasswordVault().Add(new PasswordCredential(VaultResource(source), source.CredentialKey, value));
    }

    private static void RemoveSourceSecret(WebDavSource source)
    {
        try
        {
            var vault = new PasswordVault();
            vault.Remove(vault.Retrieve(VaultResource(source), source.CredentialKey));
        }
        catch { /* A missing credential does not block index removal. */ }
    }

    private async Task ObserveSourceSaveAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* 错误由调用方 await 报告；观察任务只负责生命周期登记。 */ }
        finally { lock (_gate) _sourceSaves.Remove(work); }
    }

    private async Task DrainSourceSavesAsync()
    {
        Task[] saves;
        lock (_gate) saves = [.. _sourceSaves];
        try { await Task.WhenAll(saves).ConfigureAwait(false); }
        catch { /* 单次保存失败不跳过扫描、探活等其余资源的退出清理。 */ }
    }
}
