using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

namespace WinUIMusicPlayer.Services.AudioAnalysis;

/// <summary>只读取已提交特征，调用方负责串行化缓存切换与分析提交。</summary>
public static class AudioPreferenceScorer
{
    /// <summary>三遍流式读取已提交特征，完成曲库校准、正向采样和逐候选余弦近邻评分。</summary>
    /// <param name="repository">一个由调用方串行保护的分析仓库。</param>
    /// <param name="tracks">一份固定曲库字段快照。</param>
    /// <param name="history">一份已聚合的有效收听快照。</param>
    /// <param name="token">一个停止遍历和评分的取消令牌。</param>
    /// <param name="canScore">一个在功能关闭时拒绝继续读取的可选守卫。</param>
    /// <returns>一份仅包含有效已分析曲目的近邻支持分；没有已分析正向样本时为空。</returns>
    public static async Task<Dictionary<int, float>> ScoreAsync(AnalysisRepository repository, IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history, CancellationToken token, Func<bool>? canScore = null)
    {
        var scores = new Dictionary<int, float>();
        if (canScore?.Invoke() == false) return scores;
        var weights = new Dictionary<int, double>();
        var listening = new Dictionary<int, HomeListening>(history.Count);
        foreach (var row in history) listening[row.MusicId] = row;
        var visible = new HashSet<int>();
        foreach (var track in tracks)
        {
            visible.Add(track.Id);
            double strength = RecommendationEvidence.Strength(track, listening.GetValueOrDefault(track.Id));
            if (strength > 0) weights[track.Id] = strength;
        }
        if (weights.Count == 0) return scores;
        var mean = new double[128];
        var squares = new double[128];
        var scale = new double[128];
        var clips = new float[384];
        var vector = new float[128];
        var profile = new AudioNeighborProfile();
        int count = 0;
        int pass = 0;
        VisitAnalysisVector visitor = Process;
        for (; pass < 3; pass++)
        {
            int after = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (canScore?.Invoke() == false) return [];
                int next = await repository.VisitVectorsAsync(after, visitor, token).ConfigureAwait(false);
                if (next == after) break;
                after = next;
            }
            if (pass == 0)
            {
                if (count < 16) return scores;
                for (int i = 0; i < 128; i++) scale[i] = Math.Sqrt(Math.Max(0, squares[i] / count));
            }
            if (pass == 1)
            {
                if (!profile.HasSamples) return scores;
            }
        }
        return scores;
        void Process(int musicId, ReadOnlySpan<float> raw)
        {
            if (!visible.Contains(musicId)) return;
            foreach (float value in raw) if (!float.IsFinite(value)) return;
            if (pass == 0)
            {
                count++;
                for (int i = 0; i < 128; i++)
                {
                    double value = ((double)raw[i] + raw[128 + i] + raw[256 + i]) / 3;
                    double delta = value - mean[i];
                    mean[i] += delta / count;
                    squares[i] += delta * (value - mean[i]);
                }
                return;
            }
            if (pass == 1 && !weights.ContainsKey(musicId)) return;
            FeatureMath.Calibrate(raw, mean, scale, clips, vector);
            if (pass == 1)
            {
                profile.Add(musicId, vector, weights[musicId]);
            }
            else scores[musicId] = profile.Score(musicId, vector);
        }
    }
}
