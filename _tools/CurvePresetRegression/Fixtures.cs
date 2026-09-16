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
        private static readonly List<DispatcherQueueTimer> Timers = [];
        private bool _pending;
        public DispatcherQueueTimer() => Timers.Add(this);
        public event Action<object, object>? Tick;
        public void Start() => _pending = true;
        public void Stop() => _pending = false;
        public static void FirePending()
        {
            foreach (var timer in Timers)
                if (timer._pending) { timer._pending = false; timer.Tick?.Invoke(timer, timer); }
        }
    }
}
namespace WinUIMusicPlayer.Model
{
    public sealed record CurvePreset(string Name, string Points, bool AutoHeadroom = true, double TrimDb = 0);
    public static partial class AppSettings
    {
        public static bool IsEqualizerEnabled { get; set; }
        public static EqBand[] EqualizerBands { get; set; } = [new() { FrequencyHz = 1000 }];
        public static event EventHandler? EqUpdated;
        public static void OnEqUpdated() => EqUpdated?.Invoke(null, EventArgs.Empty);
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils { public static string GetString(string key) => key; }
}
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static object Services { get; set; } = null!;
        public static object GetLogger<T>() => new();
    }
}
namespace Microsoft.Extensions.Logging
{
    public static class FixtureLogger
    {
        public static void LogWarning(this object logger, Exception exception, string message, params object?[] args) { }
    }
}
namespace Microsoft.Extensions.DependencyInjection
{
    public static class FixtureServices
    {
        public static T GetRequiredService<T>(this object services) => (T)((Dictionary<Type, object>)services)[typeof(T)];
    }
}
namespace WinUIMusicPlayer.Services
{
    using WinUIMusicPlayer.Model;
    public sealed class LicenseService
    {
        public LicenseFeature RestrictedFeatures { get; private set; }
        public bool IsFeatureRestricted(LicenseFeature feature) => LicensePolicy.Contains(RestrictedFeatures, feature);
        public event Action? StateChanged;
        public void SetRestricted(LicenseFeature features) { RestrictedFeatures = features; StateChanged?.Invoke(); }
    }
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
    public class MusicDatabaseService
    {
        public bool FailSave { get; set; }
        public TaskCompletionSource? SaveGate { get; set; }
        public DspSettings? SavedDsp { get; private set; }
        public DeviceCorrections? SavedCorrections { get; private set; }
        public async Task SaveSettingAsync(bool throwOnError = false)
        {
            var snapshot = AppSettings.Dsp;
            if (SaveGate != null) await SaveGate.Task;
            if (FailSave) throw new IOException("Simulated settings save failure");
            SavedDsp = snapshot;
        }
        public Task SaveCurrentDeviceCorrectionsAsync()
        {
            if (FailSave) throw new IOException("Simulated correction save failure");
            SavedCorrections = AppSettings.DeviceCorrections;
            return Task.CompletedTask;
        }
        public Task SaveDeviceCorrectionsAsync(DeviceCorrections candidate)
        {
            AppSettings.DeviceCorrections = candidate.Validate();
            return SaveCurrentDeviceCorrectionsAsync();
        }
    }
    public class IpcService
    {
        public event Action? DspStateChanged;
        public DspStateSnapshot? CurrentDspState { get; private set; }
        public void ChangeOutput(string id, long? generation = null)
        {
            long next = generation ?? ((CurrentDspState?.State.OutputGeneration ?? 0) + (CurrentDspState?.State.OutputDeviceId == id ? 0 : 1));
            if (CurrentDspState?.State.OutputDeviceId != id || CurrentDspState?.State.OutputGeneration != next) LastPreview = null;
            CurrentDspState = new(1, new(0, true, 2, default, 0, 0, SampleRate: 48000, OutputDeviceId: id, OutputGeneration: next));
            DspStateChanged?.Invoke();
        }
        public int Restores { get; private set; }
        public void UpdateDsp() { Restores++; LastPreview = null; PublishLiveCorrection(); }
        public bool FailPublish { get; set; }
        public bool CorrectionSyncFailed { get; private set; }
        public event Action? CorrectionSyncChanged;
        public void SetSyncFailed(bool failed) { CorrectionSyncFailed = failed; CorrectionSyncChanged?.Invoke(); }
        public Task<(int id, string name)[]> GetWasapiDevices() => Task.FromResult(Array.Empty<(int, string)>());
        public Task<(int id, string name)[]> GetAsioDevices() => Task.FromResult(Array.Empty<(int, string)>());
        public string? GetWasapiEndpointId(int id) => null;
        public string? GetAsioEndpointId(int id) => null;
        public DeviceCorrections? PublishedCorrections { get; private set; }
        public Task UpdateDeviceCorrectionsAsync()
        {
            if (FailPublish) { SetSyncFailed(true); throw new IOException("Simulated IPC failure"); }
            PublishedCorrections = AppSettings.DeviceCorrections;
            LastPreview = null;
            PublishLiveCorrection();
            SetSyncFailed(false);
            return Task.CompletedTask;
        }
        public DspSettings? LastPreview { get; private set; }
        public void EndDspPreview() => UpdateDsp();
        public string? PreviewDeviceId { get; private set; }
        public long PreviewGeneration { get; private set; }
        public void PreviewDsp(DspSettings draft, string deviceId, long generation)
        { LastPreview = draft; PreviewDeviceId = deviceId; PreviewGeneration = generation; }
        private void PublishLiveCorrection()
        {
            if (CurrentDspState?.State is { } state && AppSettings.TryGetLiveCorrection(state.OutputDeviceId,
                state.OutputGeneration, out var settings)) PreviewDsp(settings, state.OutputDeviceId, state.OutputGeneration);
        }
    }
}
namespace WinUIMusicPlayer.ViewModel.Controls
{
    using Microsoft.UI.Dispatching;
    using WinUIMusicPlayer.Services;
    public partial class DspSettingsViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private readonly IpcService _ipc;
        private readonly MusicDatabaseService _database;
        private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
        private bool _loaded = true, _dirty;
        public bool ConvolutionRestricted { get; set; }
        public DspSettingsViewModel(IpcService ipc, MusicDatabaseService database)
        {
            _ipc = ipc; _database = database;
            _actualCorrectionDeviceId = ipc.CurrentDspState?.State.OutputDeviceId;
            ipc.DspStateChanged += () => { _actualCorrectionDeviceId = ipc.CurrentDspState?.State.OutputDeviceId; RefreshCorrectionState(); };
            ipc.CorrectionSyncChanged += OnCorrectionSyncChanged;
        }
        private void LoadValues() { }
        private Task CommitAsync()
        {
            if (!_dirty) return Task.CompletedTask;
            _dirty = false; _ipc.UpdateDsp(); return _database.SaveSettingAsync();
        }
    }
}
