// Only the surrounding shell is adapted; state, output VM, capture and persistence use production code.
using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState
    {
        public PreferencesState Preferences { get; } = new();
    }
    public sealed class PreferencesState
    {
        public AudioPreferencesState Audio { get; } = new();
        public int Latency => Audio.Latency;
        public bool IsFadeEnabled => Audio.IsFadeEnabled;
        public int DsdGain => Audio.DsdGain;
        public int DsdPcmFreq => Audio.DsdPcmFreq;
    }
}
namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static DspSettings Dsp { get; set; } = new();
        public static string OutputMode { get; set; } = "DirectSound";
        public static int BassOutputDeviceId => -1;
        public static string? WasapiEndpointId => null;
        public static int BassASIODeviceId => 0;
        public static string DeviceName => "";
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key;
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed partial class SettingsSnapshotFactory(AppState state);
}
namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel : ObservableObject
    {
        private bool _isDisposed => false;
        public AppState State { get; }
        public AppViewModel(LicenseService license, AppState state)
        {
            State = state;
            _licenseService = license;
            PurchaseLicenseCommand = new AsyncRelayCommand(license.PurchaseAsync, () => license.CanPurchase);
            _licenseStateChangedHandler = ApplyLicenseState;
            license.StateChanged += OnLicenseChanged;
            State.Preferences.Audio.PropertyChanged += (_, e) => OnPropertyChanged(e.PropertyName);
            ApplyLicenseState();
        }
        private void OnLicenseChanged() => _licenseStateChangedHandler?.Invoke();
    }
}
