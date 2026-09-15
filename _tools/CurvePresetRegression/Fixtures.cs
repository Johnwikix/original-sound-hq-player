using BassPlayerIpc.Shared;

namespace Microsoft.UI.Dispatching
{
    public class DispatcherQueue
    {
        public static DispatcherQueue GetForCurrentThread() => new();
        public DispatcherQueueTimer CreateTimer() => new();
        public bool TryEnqueue(Action action) { action(); return true; }
    }
    public class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public bool IsRepeating { get; set; }
        public event Action<object, object>? Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed record CurvePreset(string Name, string Points, bool AutoHeadroom = true, double TrimDb = 0);
}
namespace WinUIMusicPlayer.Utils
{
    public static class AppSettings { public static DspSettings Dsp { get; set; } = new(); public static DeviceCorrections DeviceCorrections { get; set; } = new(); }
    public static class ToolUtils { public static string GetString(string key) => key; }
}
namespace WinUIMusicPlayer.Services
{
    using WinUIMusicPlayer.Model;
    // Only persistence and IPC are substituted; tests call the production VM and generated commands.
    public class CurvePresetService
    {
        public List<CurvePreset> Saved { get; private set; } = [];
        public bool FailSave { get; set; }
        public TaskCompletionSource? SaveGate { get; set; }
        public Task<List<CurvePreset>> LoadAsync() => Task.FromResult(Saved.ToList());
        public async Task SaveAsync(List<CurvePreset> presets)
        {
            if (SaveGate != null) await SaveGate.Task;
            if (FailSave) throw new IOException("Simulated storage failure");
            Saved = presets.ToList();
        }
    }
    public record PlaybackFixture(int RenderKind = 0, bool IsEnabled = true, int Channels = 2, string OutputDeviceId = "");
    public record DspFixture(PlaybackFixture State);
    public class IpcService
    {
        public event Action? DspStateChanged;
        public DspFixture? CurrentDspState { get; private set; } = new(new());
        public void ChangeOutput(string id) { CurrentDspState = new(new(OutputDeviceId: id)); DspStateChanged?.Invoke(); }
        public int Restores { get; private set; }
        public void UpdateDsp() { Restores++; LastPreview = null; }
        public DspSettings? LastPreview { get; private set; }
        public void PreviewDsp(DspSettings draft) => LastPreview = draft;
    }
}
