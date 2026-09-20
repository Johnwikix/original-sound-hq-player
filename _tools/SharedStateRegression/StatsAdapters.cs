using WinUIMusicPlayer.Model.Stats;

namespace Microsoft.UI.Dispatching
{
    public enum DispatcherQueuePriority { Normal }
    public sealed class DispatcherQueue
    {
        public DispatcherQueueTimer Timer { get; } = new();
        public DispatcherQueueTimer CreateTimer() => Timer;
        public bool TryEnqueue(Action work) => TryEnqueue(DispatcherQueuePriority.Normal, work);
        public bool TryEnqueue(DispatcherQueuePriority priority, Action work)
        {
            SynchronizationContext.Current!.Post(_ => work(), null);
            return true;
        }
    }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public bool Running { get; private set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Fire()
        {
            if (!Running) return;
            Running = IsRepeating;
            Tick?.Invoke(this, new());
        }
    }
}
namespace WinUIMusicPlayer.Model.Stats
{
    public class SongPlayStat
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public int MusicId { get; set; }
        public Music? Music { get; set; }
        public int PlayCount { get; set; }
        public double TotalDurationSeconds { get; set; }
    }
    public class ArtistPlayStat : SongPlayStat { }
    public class AlbumPlayStat : SongPlayStat { public string Album { get; set; } = ""; }
}
namespace WinUIMusicPlayer.ViewModel
{
    public class AppViewModel
    {
        public bool TryFindById(int id, out Model.Music? music) { music = null; return false; }
    }
}
namespace WinUIMusicPlayer.ViewModel.Pages { public class Unused { } }
namespace WinUIMusicPlayer.Services
{
    public class PlaybackStatsService
    {
        public event Action? StatsUpdated;
        public readonly List<TaskCompletionSource<StatsSnapshot>> Queries = [];
        public int HeatmapQueries;
        public void Notify() => StatsUpdated?.Invoke();
        public Task<StatsSnapshot> GetStatsSnapshotAsync(DateTime start, DateTime end)
        {
            var completion = new TaskCompletionSource<StatsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            Queries.Add(completion);
            return completion.Task;
        }
        public Task<Dictionary<DateTime, int>> GetDailyCountsAsync(DateTime start, DateTime end)
        {
            HeatmapQueries++;
            return Task.FromResult(new Dictionary<DateTime, int>());
        }
    }
}
