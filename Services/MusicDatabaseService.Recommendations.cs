using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    /// <summary>读取首页所需的有限窗口收听摘要，不触发曲库扫描。</summary>
    public async Task<List<HomeListening>> GetHomeListeningAsync(DateTime nowUtc, CancellationToken token)
    {
        await WhenInitialized.WaitAsync(token);
        return await HomeListeningQuery.ReadAsync(_dbConnection, nowUtc, PlaybackStatsService.QualifiedRatio, token);
    }
}
