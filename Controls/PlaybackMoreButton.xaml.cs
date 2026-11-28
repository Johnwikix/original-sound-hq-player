using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.Controls;

public sealed partial class PlaybackMoreButton : UserControl
{
    private IDisposable? _session;
    private bool _menuOpen;
    private bool _editingCurve;

    public DspSettingsViewModel ViewModel { get; } = App.Services.GetRequiredService<DspSettingsViewModel>();

    public PlaybackMoreButton()
    {
        InitializeComponent();

        // WinUI tracks radio groups per UI thread, including menus from other playback bars.
        AssignRadioGroup(QuickPresets);
        AssignRadioGroup(UpsamplingOptions);
        AssignRadioGroup(PlaybackRateOptions);
    }

    private static void AssignRadioGroup(MenuFlyoutSubItem submenu)
    {
        string group = Guid.NewGuid().ToString("N");
        foreach (RadioMenuFlyoutItem item in submenu.Items)
            item.GroupName = group;
    }

    public static bool IsOptionSelected(int currentIndex, int itemIndex) => currentIndex == itemIndex;

    private void OnMenuOpening(object sender, object args)
    {
        _menuOpen = true;
        _session ??= App.Services.GetRequiredService<EditorSessions>().Attach(ViewModel.StopAsync);
        _ = ViewModel.OnViewLoadedAsync(trackCorrectionDevices: false);
    }

    private void OnMenuClosed(object sender, object args)
    {
        _menuOpen = false;
        // Keep the editor active until the menu item finishes dispatching its selection.
        DispatcherQueue.TryEnqueue(async () => await EndSessionIfIdleAsync());
    }

    private async void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _menuOpen = false;
        MoreButton.Flyout.Hide();
        await EndSessionIfIdleAsync();
    }

    private async Task EndSessionIfIdleAsync()
    {
        if (_menuOpen || _editingCurve || _session is not { } session) return;

        _session = null;
        try { await ViewModel.OnViewUnloadedAsync(); }
        finally { session.Dispose(); }
    }

    private async void OpenEqualizer_Click(object sender, RoutedEventArgs args) =>
        await App.Services.GetRequiredService<LibraryNavigationService>().ShowEqualizerAsync();

    private void ApplyPreset_Click(object sender, RoutedEventArgs args) =>
        ViewModel.QuickPresetIndex = QuickPresets.Items.IndexOf((MenuFlyoutItemBase)sender);

    private void SelectUpsampling_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SelectUpsampling(UpsamplingOptions.Items.IndexOf((MenuFlyoutItemBase)sender));

    private void SelectPlaybackRate_Click(object sender, RoutedEventArgs args) =>
        ViewModel.SelectPlaybackRate(PlaybackRateOptions.Items.IndexOf((MenuFlyoutItemBase)sender));

    private async void EditCurve_Click(object sender, RoutedEventArgs args)
    {
        if (_editingCurve || !ViewModel.CanEditCorrection || XamlRoot is null) return;

        _editingCurve = true;
        try
        {
            _ = ViewModel.StartTrackingCorrectionDevicesAsync();
            await ConvolutionCurveDialog.ShowEditorAsync(ViewModel, this);
        }
        finally
        {
            _editingCurve = false;
            await EndSessionIfIdleAsync();
        }
    }
}
