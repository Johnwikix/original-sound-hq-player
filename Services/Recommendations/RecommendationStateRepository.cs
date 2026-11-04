using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>复用曲库连接；单行SQLite提交保存有界快照，读取失败不覆盖已有状态。</summary>
internal sealed class RecommendationStateRepository(SQLiteAsyncConnection database)
{
    internal async Task<HomeRecommendationState> LoadAsync(DateTime nowUtc, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await database.ExecuteAsync("CREATE TABLE IF NOT EXISTS HomeRecommendationState (Id INTEGER PRIMARY KEY CHECK(Id=1), StateJson TEXT NOT NULL)").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var rows = await database.QueryScalarsAsync<string>("SELECT StateJson FROM HomeRecommendationState WHERE Id=1").ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (rows.Count == 0) return HomeRecommendationState.CreateNew();
        var state = JsonSerializer.Deserialize(rows[0], HomeRecommendationStateJsonContext.Default.HomeRecommendationState)
            ?? throw new InvalidDataException("Empty home recommendation state.");
        return state.Normalize(nowUtc);
    }

    internal Task SaveAsync(HomeRecommendationState state, DateTime nowUtc)
    {
        var snapshot = state.Normalize(nowUtc);
        string json = JsonSerializer.Serialize(snapshot, HomeRecommendationStateJsonContext.Default.HomeRecommendationState);
        // 不取消已开始的提交；调用方在退出前等待实际SQL结束。
        return database.ExecuteAsync("INSERT INTO HomeRecommendationState (Id,StateJson) VALUES(1,?) ON CONFLICT(Id) DO UPDATE SET StateJson=excluded.StateJson", json);
    }
}
