using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

/// <summary>属性窗口拥有的封面会话。原始字节在后台读取，返回调用方 UI 上下文创建和发布；关闭取消读取并断开图片、字节及视图回调。</summary>
public sealed class WindowArtworkSession : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private bool _closed;
    public byte[]? Data { get; private set; }
    public BitmapImage? Image { get; private set; }
    public event Action? Updated;

    /// <returns>当前请求正常完成返回 true（包括按参数保留已有封面的空结果）；关闭、取消或已被替换返回 false。</returns>
    public async Task<bool> LoadAsync(Func<CancellationToken, Task<byte[]?>> read, CancellationToken token = default, bool keepExistingOnEmpty = false)
    {
        if (_closed) return false;
        _request?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        _request = request;
        BitmapImage? image = null;
        try
        {
            // async 读取器在第一次 await 前仍可能同步解析音频；该委托只能读取字节，不访问 UI。
            // Task.Run 展开并等待完整读取任务，取消等待不冒充同步 I/O 已结束。
            var readToken = request.Token;
            var data = await Task.Run(() => read(readToken), readToken);
            request.Token.ThrowIfCancellationRequested();
            if (_closed || !ReferenceEquals(_request, request)) return false;
            if (keepExistingOnEmpty && data is not { Length: > 0 }) return true;
            image = data is { Length: > 0 } ? await ToolUtils.ConvertByteArrayToBitmapImage(data) : null;
            request.Token.ThrowIfCancellationRequested();
            if (_closed || !ReferenceEquals(_request, request)) return false;
            Data = data;
            Image = image;
            image = null;
            Updated?.Invoke();
            return true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { return false; }
        finally
        {
            image = null;
            if (ReferenceEquals(_request, request)) _request = null;
        }
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        Updated = null;
        Image = null;
        Data = null;
        try { _lifetime.Cancel(); }
        catch (AggregateException exception) { App.GetLogger<WindowArtworkSession>().LogWarning(exception, "封面取消回调失败，继续关闭清理"); }
        finally { _lifetime.Dispose(); }
    }

    // BitmapImage 没有 IDisposable/IClosable；由绑定移除 Source 并断开会话引用后回收。
}
