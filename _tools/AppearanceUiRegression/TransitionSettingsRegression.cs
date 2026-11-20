using AppearanceUiRegression;
using BassPlayerIpc.Shared;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel.Controls;

internal static class TransitionSettingsRegression
{
    internal static async Task RunAsync(Panel host)
    {
        DspSettings baseline = AppSettings.Dsp;
        AppSettings.Dsp = new()
        {
            TransitionStyle = TrackTransitionStyle.Smart,
            CrossfadeSeconds = 7,
            CompressorEnabled = true
        };
        using var vm = new PlaybackTransitionsViewModel();
        var view = new TransitionControl(vm) { IsFadeEnabled = true };
        host.Children.Add(view);
        try
        {
            await Task.Delay(100);
            var expander = (SettingsExpander)view.FindName("TransitionExpander");
            expander.IsExpanded = true;
            var mode = (ComboBox)view.FindName("TransitionMode");
            var duration = (ComboBox)view.FindName("Duration");
            var durationCard = (SettingsCard)view.FindName("DurationCard");
            var manual = (ToggleSwitch)view.FindName("ManualFades");
            Check(mode.Items.Count == 4 && mode.SelectedIndex == 3, "The four-mode selector did not restore Smart.");
            Check(duration.Items.Count == 12 && duration.SelectedIndex == 6 && durationCard.Visibility == Visibility.Visible,
                "Smart did not restore seven seconds in a visible duration card.");
            Check(durationCard.Description is string { Length: > 0 } && manual.IsOn, "Smart help or saved manual fades were lost.");

            mode.SelectedIndex = 0;
            Check(!AppSettings.Dsp.GaplessPlayback && durationCard.Visibility == Visibility.Collapsed
                && AppSettings.Dsp.CrossfadeSeconds == 7 && manual.IsOn, "Normal modified independent settings or exposed duration.");
            mode.SelectedIndex = 1;
            Check(AppSettings.Dsp is { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless }
                && durationCard.Visibility == Visibility.Collapsed, "Gapless exposed duration or did not activate.");
            mode.SelectedIndex = 2;
            Check(AppSettings.Dsp.TransitionStyle == TrackTransitionStyle.Crossfade && durationCard.Visibility == Visibility.Visible
                && duration.SelectedIndex == 6, "Fixed crossfade failed to restore duration.");
            duration.SelectedIndex = 11;
            Check(AppSettings.Dsp.CrossfadeSeconds == 12 && AppSettings.Dsp.CompressorEnabled && manual.IsOn,
                "Choosing twelve seconds lost an independent preference.");
            manual.IsOn = false;
            Check(!view.IsFadeEnabled && AppSettings.Dsp.CrossfadeSeconds == 12, "Manual fade toggle modified the automatic transition.");
            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Smart, CrossfadeSeconds = 1 };
            Check(mode.SelectedIndex == 3 && duration.SelectedIndex == 0, "External preference restoration did not update both controls.");
            expander.IsExpanded = false;
            expander.IsExpanded = true;
            Check(mode.SelectedIndex == 3 && duration.SelectedIndex == 0, "Reopening the expander modified the restored values.");
        }
        finally
        {
            host.Children.Remove(view);
            AppSettings.Dsp = baseline;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
