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
            var duration = (Slider)view.FindName("Duration");
            var durationCard = (SettingsCard)view.FindName("DurationCard");
            var manual = (ToggleSwitch)view.FindName("ManualFades");
            Check(mode.Items.Count == 4 && mode.SelectedIndex == 3, "The four-mode selector did not restore Smart.");
            Check(duration is { Minimum: 300, Maximum: 3000, StepFrequency: 100, Value: 1000 }
                && durationCard.Visibility == Visibility.Visible, "Smart did not show the millisecond range and default.");
            var description = durationCard.Description as string;
            Check(description is { Length: > 0 } && manual.IsOn, "Smart help or saved manual fades were lost.");

            mode.SelectedIndex = 0;
            Check(!AppSettings.Dsp.GaplessPlayback && durationCard.Visibility == Visibility.Collapsed
                && AppSettings.Dsp.CrossfadeMilliseconds == 1000 && manual.IsOn, "Normal modified independent settings or exposed duration.");
            mode.SelectedIndex = 1;
            Check(AppSettings.Dsp is { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Gapless }
                && durationCard.Visibility == Visibility.Collapsed, "Gapless exposed duration or did not activate.");
            mode.SelectedIndex = 2;
            Check(AppSettings.Dsp.TransitionStyle == TrackTransitionStyle.Crossfade && durationCard.Visibility == Visibility.Visible
                && duration.Value == 1000, "Fixed crossfade failed to restore duration.");
            Check(durationCard.Description is "", "The Smart description was shown in Fixed mode.");
            duration.Value = 1300;
            Check(AppSettings.Dsp.CrossfadeMilliseconds == 1300 && AppSettings.Dsp.CompressorEnabled && manual.IsOn,
                "Choosing 1300 ms did not reach settings or lost an independent preference.");
            duration.Value = 3000;
            Check(AppSettings.Dsp.CrossfadeMilliseconds == 3000, "The slider maximum did not reach settings.");
            manual.IsOn = false;
            Check(!view.IsFadeEnabled && AppSettings.Dsp.CrossfadeMilliseconds == 3000, "Manual fade toggle modified the automatic transition.");
            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Smart, CrossfadeMilliseconds = 300 };
            Check(mode.SelectedIndex == 3 && duration.Value == 300 && Equals(durationCard.Description, description),
                "External preference restoration did not update both controls and the Smart description.");
            expander.IsExpanded = false;
            expander.IsExpanded = true;
            Check(mode.SelectedIndex == 3 && duration.Value == 300, "Reopening the expander modified the restored values.");
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
