using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public sealed partial class WebDavLibraryService
{
    private readonly Dictionary<int, (CancellationTokenSource Cancel, Task Work)> _scans = [];

    public Task ScanAsync(WebDavSource source) => ScanAsync(source, refreshScope: false);

    private Task ScanAsync(WebDavSource source, bool refreshScope)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested || !source.Enabled || _changingSources.Contains(source.Id)) return Task.CompletedTask;
            if (_scans.TryGetValue(source.Id, out var previous) && !previous.Work.IsCompleted) return previous.Work;
            previous.Cancel?.Dispose();
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var work = Task.Run(() => RunScanAsync(source, refreshScope, cancel.Token));
            _scans[source.Id] = (cancel, work);
            return work;
        }
    }

    private async Task RunScanAsync(WebDavSource source, bool refreshScope, CancellationToken token)
    {
        if (refreshScope)
        {
            // The committed scope must be visible even if the follow-up scan is paused.
            await RefreshSavedScopeAsync(source.Id, _stop.Token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
        }
        if (!CanPublishCommittedSource) return;
        if (source.Kind == RemoteSourceKind.WebDav)
            await ScanCoreAsync(source, token).ConfigureAwait(false);
        else
            await ScanCatalogAsync(source, token).ConfigureAwait(false);
    }

    public async Task CancelScanAsync(int sourceId)
    {
        Task? work = null;
        lock (_gate)
        {
            if (_scans.TryGetValue(sourceId, out var scan))
            {
                scan.Cancel.Cancel();
                work = scan.Work;
            }
        }
        if (work is not null) await work.ConfigureAwait(false);
    }
}
