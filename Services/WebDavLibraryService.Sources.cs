using CommunityToolkit.WinUI;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private readonly HashSet<Task> _sourceSaves = [];

    public Task SaveSourceAsync(WebDavSource source, string password)
    {
        lock (_gate)
        {
            _stop.Token.ThrowIfCancellationRequested();
            // PasswordVault.Add 是同步 WinRT 调用，冷启动可耗时数秒；整个持久化入口离开 UI 线程。
            // 不把取消传给 Task.Run：凭据开始写入后仍须完成数据库保存，退出等待真实任务收尾。
            var work = Task.Run(() => SaveSourceCoreAsync(source, password));
            _sourceSaves.Add(work);
            _ = ObserveSourceSaveAsync(work);
            return work;
        }
    }

    private async Task SaveSourceCoreAsync(WebDavSource source, string password)
    {
        bool isNew = source.Id == 0;
        source.BaseUri = WebDavTransport.NormalizeRoot(source.BaseUri).AbsoluteUri;
        if (string.IsNullOrWhiteSpace(source.Name)) throw new WebDavException("NameRequired");
        foreach (var existing in await database.GetWebDavSourcesAsync().ConfigureAwait(false))
            if (existing.Id != source.Id && existing.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) throw new WebDavException("NameExists");
        if (source.UserName.Length != 0) new PasswordVault().Add(new PasswordCredential("OriginalSoundPlayer.WebDav", source.CredentialKey, password));
        await database.SaveWebDavSourceAsync(source).ConfigureAwait(false);
        // 新来源尚无曲目，由扫描批次增量发布；编辑来源仍需立即收敛已排除目录的可见性。
        if (!isNew && _library is not null && !_stop.IsCancellationRequested)
            await _library.RefreshSongsSourceAsync(_stop.Token).ConfigureAwait(false);
        if (_stop.IsCancellationRequested) return;
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            if (_stop.IsCancellationRequested) return;
            if (source.Enabled) _ = ProbeAsync(source);
            SourcesChanged?.Invoke();
        }).ConfigureAwait(false);
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
