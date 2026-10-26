using AppearanceUiRegression;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Controls;

internal static class PcmSettingsRegression
{
    internal static async Task RunAsync(Panel host)
    {
        var vm = new DspSettingsViewModel();
        var view = new PcmControl(vm);
        host.Children.Add(view);
        try
        {
            await Task.Delay(100);
            ComboBox mode = (ComboBox)view.FindName("ResampleMode");
            ComboBox target = (ComboBox)view.FindName("ResampleTarget");
            NumberBox ceiling = (NumberBox)view.FindName("Ceiling");
            ComboBox preset = (ComboBox)view.FindName("CrossfeedPreset");
            NumberBox amount = (NumberBox)view.FindName("CrossfeedAmount");
            NumberBox cutoff = (NumberBox)view.FindName("CrossfeedCutoff");
            vm.LoadSavedRate();
            vm.PublishPcmState(true);
            Check(!target.IsEnabled, "Source mode must disable the target");
            mode.SelectedIndex = 1;
            Check(target.IsEnabled && vm.ResampleTargetLabel == "Target sample rate", "Fixed rate did not enable target");
            target.SelectedIndex = 3;
            mode.SelectedIndex = 2;
            Check(target.IsEnabled && vm.ResampleTargetLabel == "Integer-multiple rate limit", "Integer mode label did not update");
            mode.SelectedIndex = 0;
            Check(!target.IsEnabled && target.SelectedIndex == 3, "Source mode lost the stored target");
            Check(!ceiling.IsEnabled, "Both peak consumers off must disable ceiling");
            vm.NormalizeLoudness = true;
            Check(ceiling.IsEnabled, "Normalization alone must enable shared ceiling");
            ceiling.Value = -6;
            Check(vm.TruePeakCeilingDb == -6, "Ceiling input did not reach the production ViewModel");
            vm.NormalizeLoudness = false;
            vm.TruePeakLimiterEnabled = true;
            Check(ceiling.IsEnabled && ceiling.Value == -6, "Limiter alone lost the shared value");
            vm.PublishPcmState(true, canSeek: false);
            Check(!((ToggleSwitch)view.FindName("Limiter")).IsEnabled && ceiling.IsEnabled,
                "Nonseekable stream must retain hot ceiling editing without allowing topology changes");
            amount.Value = 30;
            Check(vm.CrossfeedAmount == 30 && preset.SelectedIndex == -1 && preset.PlaceholderText == "Custom",
                "Custom crossfeed did not display a meaningful preset state");
            Check(cutoff.IsEnabled, "Nonzero crossfeed must enable cutoff");
            preset.SelectedIndex = 0;
            Check(amount.Value == 0 && !cutoff.IsEnabled, "Off preset did not disable cutoff");
            vm.PublishPcmState(true, channels: 6);
            Check(!preset.IsEnabled && !amount.IsEnabled && !cutoff.IsEnabled, "Multichannel crossfeed controls remained editable");
            vm.PublishPcmState(false);
            Check(!target.IsEnabled && !ceiling.IsEnabled && ceiling.Value == -6, "DSP bypass lost or exposed dependent settings");
        }
        finally { host.Children.Remove(view); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
