namespace AudioPlayer.AudioEnhancement;

/// <summary>声道记录中的修复历史、能量和工作缓冲区句柄。</summary>
internal static class ChannelStateLayout
{
    internal const int BandRepairInput = 0;
    internal const int BandRepairAnalysis = 4;
    internal const int LowBandHistory = 1280;
    internal const int OutputFilterHistory = 1312;
    internal const int HighBandHistory = 1344;
    internal const int EmphasisFilterHistory = 1376;
    internal const int RepairSignalHistory = 1384;
    internal const int RepairSignal = 1488;
    internal const int RetainedRepairSamples = 1896;
    internal const int BandEnergyHistory = 2016;
    internal const int TotalEnergyHistory = 2032;
    internal const int BandEnergyHistoryTail = 2136;
    internal const int ComparisonEnergies = 2176;
    internal const int PredictedGains = 2192;
}
