using SQLite;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>在 SQLite 中聚合有限窗口历史，避免把完整会话表物化到首页。</summary>
public static class HomeListeningQuery
{
    /// <summary>读取最近九十天的有效收听，三十天前的单次信号衰减至一半。</summary>
    public static async Task<List<HomeListening>> ReadAsync(SQLiteAsyncConnection database, DateTime nowUtc,
        double qualifiedRatio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // 使用 SQLite 基础算术实现双曲衰减，不要求原生库启用额外数学函数。
        var rows = await database.QueryAsync<HomeListening>(
            "SELECT MusicId, COUNT(*) AS Count, MAX(StartedAt) AS LastPlayedTicks, " +
            "SUM(MIN(1.0, DurationPlayedMs / TotalDurationMs) / " +
            "(1.0 + MAX(0.0, (? - StartedAt) / 864000000000.0) / 30.0)) AS Weight " +
            "FROM PlaybackHistory WHERE StartedAt >= ? AND StartedAt <= ? AND MusicId > 0 " +
            "AND TotalDurationMs > 0 AND DurationPlayedMs >= TotalDurationMs * ? GROUP BY MusicId",
            nowUtc.Ticks, nowUtc.AddDays(-90).Ticks, nowUtc.Ticks, qualifiedRatio).ConfigureAwait(false);
        // 等待查询实际完成后检查取消；不能把取消等待当作数据库工作已经结束。
        token.ThrowIfCancellationRequested();
        return rows;
    }
}
