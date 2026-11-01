using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using AppearanceUiRegression;
using WinUIMusicPlayer.ViewModel.Controls;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Model;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApplication();
        });
    }
}

internal sealed partial class RegressionApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider[] _metadata =
    [
        new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
        new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider()
    ];
    public IXamlType GetXamlType(Type type)
    {
        foreach (var provider in _metadata)
            if (provider.GetXamlType(type) is { } result) return result;
        return null!;
    }
    public IXamlType GetXamlType(string fullName)
    {
        foreach (var provider in _metadata)
            if (provider.GetXamlType(fullName) is { } result) return result;
        return null!;
    }
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    public RegressionApplication()
    {
        UnhandledException += (_, args) => File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: unhandled " + args.Exception);
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var vm = new DspSettingsViewModel();
        var control = new RateControl(vm);
        var host = new Grid();
        var window = new Window { Content = host };
        host.Children.Add(control);
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        window.Activate();
        string result;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            await Task.Delay(300);
            await BackgroundSettingsRegression.RunAsync(host);
            await SpectralSettingsRegression.RunAsync(host);
            await PcmSettingsRegression.RunAsync(host);
            var initial = new DspSettingsViewModel();
            if (initial.PlaybackRate != 1 || initial.PlaybackRateIndex != 3)
                throw new Exception($"Before Loaded: rate={initial.PlaybackRate}; index={initial.PlaybackRateIndex}; expected 1× at index 3");
            initial.PlaybackRateIndex = 0;
            if (initial.PlaybackRate != 1) throw new Exception("Initialization wrote the first item back into the saved playback rate.");
            result = $"rate={vm.PlaybackRate}; index={vm.PlaybackRateIndex}; selection={control.Combo.SelectedIndex}";
            if (vm.PlaybackRate != 1 || control.Combo.SelectedIndex != 3) throw new Exception(result);
            double[] rates = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 5];
            for (int i = 0; i < rates.Length; ++i)
            {
                control.Combo.SelectedIndex = i;
                if (vm.PlaybackRate != rates[i]) throw new Exception($"User selection {i} did not write rate {rates[i]}");
                AppSettings.Dsp = new() { PlaybackRate = rates[i] };
                var savedVm = new DspSettingsViewModel();
                var savedControl = new RateControl(savedVm);
                await AttachAsync(host, savedControl);
                await Task.Delay(50);
                if (savedVm.PlaybackRate != rates[i] || savedControl.Combo.SelectedIndex != i)
                    throw new Exception($"Saved rate {rates[i]} after Loaded: rate={savedVm.PlaybackRate}; vmIndex={savedVm.PlaybackRateIndex}; selection={savedControl.Combo.SelectedIndex}");
                host.Children.Remove(savedControl);
            }
            AppSettings.Dsp = new() { PlaybackRate = 1.25 };
            var customVm = new DspSettingsViewModel();
            var customControl = new RateControl(customVm);
            await AttachAsync(host, customControl);
            await Task.Delay(50);
            if (customVm.PlaybackRate != 1.25 || customControl.Combo.SelectedIndex != -1 || customControl.Combo.PlaceholderText != "1.25×")
                throw new Exception("Legacy custom rate did not survive initialization");
            customControl.Combo.SelectedIndex = 3;
            if (customVm.PlaybackRate != 1) throw new Exception("Choosing 1× from a legacy custom rate failed");
            host.Children.Remove(customControl);

            var background = new WindowBackgroundImage();
            int failures = 0;
            background.LoadFailed += (_, _) => ++failures;
            host.Children.Insert(0, background);
            await WaitUntilAsync(() => background.IsLoaded);
            string fixture = Path.Combine(AppContext.BaseDirectory, "background.png");
            // A real WIC-decodable bitmap; the composition surface and GPU blur are production code.
            await File.WriteAllBytesAsync(fixture, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aFKYAAAAASUVORK5CYII="));
            background.SourcePath = fixture;
            await WaitUntilAsync(() => background.HasImage || failures != 0);
            if (!background.HasImage) throw new Exception("Valid background did not load");
            var visual = ElementCompositionPreview.GetElementChildVisual(background);
            background.BlurAmount = 0;
            background.BlurAmount = 100;
            if (!ReferenceEquals(visual, ElementCompositionPreview.GetElementChildVisual(background)))
                throw new Exception("Blur change rebuilt the image resources");
            background.SourcePath = fixture + ".missing";
            background.SourcePath = fixture;
            await WaitUntilAsync(() => background.HasImage);
            if (failures != 0) throw new Exception("Stale image request published an error");
            background.SourcePath = fixture + ".missing";
            await WaitUntilAsync(() => failures == 1);
            if (background.HasImage) throw new Exception("Failed image did not fall back");
            string invalid = Path.Combine(AppContext.BaseDirectory, "invalid.png");
            await File.WriteAllTextAsync(invalid, "not an image");
            background.SourcePath = invalid;
            await WaitUntilAsync(() => failures >= 2);
            background.SourcePath = fixture;
            await WaitUntilAsync(() => background.HasImage);
            background.SourcePath = string.Empty;
            if (background.HasImage || ElementCompositionPreview.GetElementChildVisual(background) is not null)
                throw new Exception("Clearing background retained its visual");
            background.SourcePath = fixture;
            host.Children.Remove(background);
            await Task.Delay(100);
            if (background.HasImage) throw new Exception("Unloaded image published a late result");
            await AttachAsync(host, background);
            await WaitUntilAsync(() => background.HasImage);
            background.Dispose();
            background.Dispose();
            if (background.HasImage || ElementCompositionPreview.GetElementChildVisual(background) is not null)
                throw new Exception("Dispose retained the background resources");
            File.Delete(fixture);
            File.Delete(invalid);
            result = "PASS: shipping DSP upsampling card, master enable/disable, three choices and two-way selection persistence\nPASS: " + result;
            result += "\nPASS: all 9 saved/user rates, legacy custom rate, initialization guard\nPASS: real composition load, blur, stale request, missing/corrupt image, clear, unload/reload and idempotent disposal";
            result += "\nPASS: shipping background SettingsExpander expansion, collapse/re-expansion and warning visibility";
            result += "\nPASS: shipping PCM compiled bindings; resample dependency/labels, shared peak ceiling, nonseekable stream, custom crossfeed and multichannel/bypass guards";
        }
        catch (Exception ex) { result = "FAIL: " + ex; Environment.ExitCode = 1; }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), result);
        window.Close();
        Exit();
    }

    private static async Task AttachAsync(Panel host, FrameworkElement element)
    {
        var loaded = new TaskCompletionSource();
        RoutedEventHandler handler = (_, _) => loaded.TrySetResult();
        element.Loaded += handler;
        try
        {
            host.Children.Add(element);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { element.Loaded -= handler; }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
