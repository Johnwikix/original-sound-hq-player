using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;
/// <summary>UI harness uses explicit empty, completed sources; worker/SQLite are tested separately.</summary>
internal sealed class FixtureEnrichment : IAudioRecommendationSource, ICommunityRecommendationSource
{
    public event Action? ResultsChanged { add { } remove { } }
    public event Action? Changed { add { } remove { } }
    public CommunitySignals Snapshot => CommunitySignals.Empty;
    public Task<Dictionary<int,float>> GetScoresAsync(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history, CancellationToken token) => Task.FromResult(new Dictionary<int,float>());
}
