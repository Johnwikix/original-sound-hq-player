using System;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>元数据、音频与社区种子共用的正向偏好口径；未收听不作为负反馈。</summary>
public static class RecommendationEvidence
{
    public static double Strength(bool favorite, double listeningWeight)
    {
        double listening = double.IsFinite(listeningWeight) && listeningWeight > 0
            ? Math.Min(3, Math.Log(1 + listeningWeight)) : 0;
        return listening + (favorite ? 2 : 0);
    }

    public static double Strength(bool favorite, HomeListening? listening) => Strength(favorite, listening?.Weight ?? 0);

    public static double Strength(RecommendationTrack track, HomeListening? listening) =>
        Strength(track.IsFavorite || track.IsInPlaylist, listening);

    public static double Signal(float value) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
