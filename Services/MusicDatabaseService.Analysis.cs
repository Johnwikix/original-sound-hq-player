using WinUIMusicPlayer.Services.AudioAnalysis;
namespace WinUIMusicPlayer.Services;
public partial class MusicDatabaseService
{
    /// <summary>获取由主数据库拥有连接的音频特征仓库。</summary>
    public AnalysisRepository Analysis { get; private set; } = null!;
}
