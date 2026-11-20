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
            Check(duration.Items.Count == 5 && duration.SelectedIndex == 1 && durationCard.Visibility == Visibility.Visible,
                "Smart did not show five durations and default to two seconds.");
            for (int index = 0; index < duration.Items.Count; index++)
                Check(duration.Items[index] as string == (index + 1).ToString(), "Duration must offer only whole seconds from one to five.");
            Check(durationCard.Description is string { Length: > 0 } && manual.IsOn, "Smart help or saved manual fades were lost.");

            mode.SelectedIndex = 0;
            Check(!AppSettings.Dsp.GaplessPlayback && durationCard.Visibility == Visibility.Collapsed
                && AppSettings.Dsp.CrossfadeSeconds == 2 && manual.IsOn, "Normal modified independent settings or exposed duration.");
            mode.SelectedIndex = 1;
            Check(AppSettings.Dsp is { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless }
                && durationCard.Visibility == Visibility.Collapsed, "Gapless exposed duration or did not activate.");
            mode.SelectedIndex = 2;
            Check(AppSettings.Dsp.TransitionStyle == TrackTransitionStyle.Crossfade && durationCard.Visibility == Visibility.Visible
                && duration.SelectedIndex == 1, "Fixed crossfade failed to restore duration.");
            duration.SelectedIndex = 4;
            Check(AppSettings.Dsp.CrossfadeSeconds == 5 && AppSettings.Dsp.CompressorEnabled && manual.IsOn,
                "Choosing five seconds lost an independent preference.");
            manual.IsOn = false;
            Check(!view.IsFadeEnabled && AppSettings.Dsp.CrossfadeSeconds == 5, "Manual fade toggle modified the automatic transition.");
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
