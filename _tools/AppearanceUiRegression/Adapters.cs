using BassPlayerIpc.Shared;
using Microsoft.UI.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinUIMusicPlayer.Model
{
    public static partial class AppSettings { }
}
namespace WinUIMusicPlayer.Services
{
    public enum LicenseFeature { None, Crossfeed, LoudnessNormalization }
}
namespace WinUIMusicPlayer.ViewModel.Controls
{
    public partial class DspSettingsViewModel : ObservableObject
    {
        private bool _syncing = true, _loaded;
        private DspState? _lastState;
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
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static Visibility BoolToVisibilityConverter(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    }

    public static class ToolUtils
    {
        private static readonly Dictionary<string, string> Resources = System.Xml.Linq.XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "DspResources.resw")).Root!.Elements("data")
            .ToDictionary(x => (string)x.Attribute("name")!, x => (string)x.Element("value")!);
        public static string GetString(string key) => Resources[key];
    }
}
