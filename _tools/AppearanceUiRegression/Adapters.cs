using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings { public static DspSettings Dsp { get; set; } = new(); }
}
namespace WinUIMusicPlayer.Services
{
    public enum LicenseFeature { None, Crossfeed, LoudnessNormalization }
}
namespace WinUIMusicPlayer.ViewModel.Controls
{
    public partial class DspSettingsViewModel : ObservableObject
    {
        private bool _syncing = true, _loaded, _dirty;
        private DspState? _lastState;
        private readonly FakeTimer _commitTimer = new();
        public bool EffectsActive { get; private set; } = true;
        public bool NormalizeEditable => EffectsActive;
        public bool CrossfeedEditable => EffectsActive;
        public bool StereoSupported { get; private set; } = true;
        public void PublishPcmState(bool active, int channels = 2, bool canSeek = true)
        {
            EffectsActive = active;
            StereoSupported = channels is 0 or 2;
            _lastState = new DspState() with { CanChangePlaybackRate = canSeek };
            OnPropertyChanged(nameof(EffectsActive));
            OnPropertyChanged(nameof(NormalizeEditable));
            OnPropertyChanged(nameof(PcmConfigurationEditable));
            RefreshPcmAvailability();
        }
        public void LoadSavedRate()
        {
            _syncing = true;
            PlaybackRate = WinUIMusicPlayer.Model.AppSettings.Dsp.PlaybackRate;
            _loaded = true;
            _syncing = false;
        }
        private void SettingChanged(WinUIMusicPlayer.Services.LicenseFeature feature) { }
        private Task CommitAsync() => Task.CompletedTask;
        private sealed class FakeTimer { public void Stop() { } }
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        private static readonly Dictionary<string, string> Resources = System.Xml.Linq.XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DspResources.resw")).Root!.Elements("data")
            .ToDictionary(x => (string)x.Attribute("name")!, x => (string)x.Element("value")!);
        public static string GetString(string key) => Resources[key];
    }
}
