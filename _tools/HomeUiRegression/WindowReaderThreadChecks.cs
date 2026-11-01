using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

internal sealed partial class HomePolishWindow
{
    private async Task CheckWindowReaderThreading()
    {
        var bytes = await ToolUtils.GetRawImage(_songs[0]);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool readOnUi = false, publishOnUi = false;
        using var session = new WindowArtworkSession();
        session.Updated += () => publishOnUi = DispatcherQueue.HasThreadAccess;
        Task<byte[]?> Read(CancellationToken token)
        {
            readOnUi = DispatcherQueue.HasThreadAccess;
            entered.TrySetResult();
            // An incorrect UI invocation returns immediately so the failing baseline cannot deadlock.
            if (!readOnUi && !release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("UI did not release the synchronous reader");
            token.ThrowIfCancellationRequested();
            return Task.FromResult(bytes);
        }
        var loading = session.LoadAsync(Read);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(!readOnUi, "window byte reader executes away from the XAML UI thread before its first await");
            bool heartbeat = false;
            DispatcherQueue.TryEnqueue(() => heartbeat = true);
            await Wait(() => heartbeat);
            Assert(!loading.IsCompleted, "UI dispatcher remains responsive while a synchronous byte reader is still blocked");
            release.Set();
            Assert(await loading && publishOnUi && session.Image is { PixelWidth: > 0 },
                "only byte I/O runs in the background; native BitmapImage creation and publication return to UI");
        }
        finally { release.Set(); await loading; }

        using var releaseClosed = new ManualResetEventSlim();
        var closedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closed = new WindowArtworkSession();
        CancellationToken received = default;
        var pending = closed.LoadAsync(token =>
        {
            received = token;
            closedEntered.TrySetResult();
            if (!releaseClosed.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("closed read was not drained");
            return Task.FromResult(bytes);
        });
        try
        {
            await closedEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closed.Dispose();
            Assert(received.IsCancellationRequested && !pending.IsCompleted,
                "closing cancels synchronous background I/O without pretending its real work has ended");
            releaseClosed.Set();
            Assert(!await pending && closed.Image is null && closed.Data is null, "late bytes cannot publish after owner disposal");
        }
        finally { releaseClosed.Set(); await pending; }
    }
}
