using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace WinUIMusicPlayer.Services.LastFm;
/// <summary>社区相似关系，不是新曲目的播放地址。</summary>
public sealed record LastFmSimilarity(string Artist, string Title, float Match);
public sealed partial class LastFmService
{
    /// <summary>复用账户隔离、取消和共享请求槽；不阻塞配置命令锁。</summary>
    public async Task<List<LastFmSimilarity>> GetSimilarAsync(string artist, string? title, long revision, CancellationToken cancellation)
    {
        var request = await GetRequestAsync(revision).ConfigureAwait(false);
        if (request is null) throw new OperationCanceledException();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, request.Value.Token, _stop.Token);
        await _network.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var result = await api.SimilarAsync(request.Value.Identity.Credentials, artist, title, linked.Token).ConfigureAwait(false);
            if (!CanSend(revision)) throw new OperationCanceledException();
            return result;
        }
        finally { _network.Release(); }
    }
}
