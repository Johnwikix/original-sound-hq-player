using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView.Settings;
using WinUIMusicPlayer.ViewModel;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new RegressionApp();
        });
    }
}
internal sealed partial class RegressionApp : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider _native = new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider();
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(typeof(Program).Assembly.GetTypes().Single(t => t.FullName!.EndsWith(".XamlMetaDataProvider")))!;
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private readonly List<string> _results = [];
    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _results.Add("PASS: " + name);
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? window = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            using var provider = new ServiceCollection().AddSingleton<SettingsViewModel>().BuildServiceProvider();
            WinUIMusicPlayer.App.Services = provider;
            var frame = new Frame();
            window = new Window { Content = frame };
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            frame.Navigate(typeof(SettingsPage));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(ProbeSection.Created.Count == 1 && ProbeSection.Created[0] is GeneralSettingsControl,
                $"First entry creates only General (actual: {string.Join(", ", ProbeSection.Created.Select(c => c.GetType().Name))})");
            Check(ProbeSection.Created[0].ActualWidth >= frame.ActualWidth - 4, "Deferred content stretches across the page");
            var page = (SettingsPage)frame.Content;
            var selector = (SelectorBar)page.FindName("SectionSelector");
            for (int i = 1; i < selector.Items.Count; i++)
            {
                selector.SelectedItem = selector.Items[i];
                await Settle(frame);
                Check(ProbeSection.Created.Count == i + 1, "Selecting " + selector.Items[i].Tag + " creates only that section");
                Check(ProbeSection.Created[^1].Loads == 1, "Selected section loads once");
                Check(ProbeSection.Created[^1].ActualWidth >= frame.ActualWidth - 4, "Selected section keeps the existing full width layout");
            }
            var dsp = ProbeSection.Created.OfType<DspSettingsControl>().Single();
            Check(dsp.Unloads == 1, "Leaving DSP raises Unloaded for session cleanup");
            selector.SelectedItem = selector.Items[2];
            await Settle(frame);
            Check(ProbeSection.Created.Count == 6 && dsp.Loads == 2, "Returning to DSP reuses its view and runs Loaded again");
            frame.Navigate(typeof(Page));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(dsp.Unloads == 2, "Leaving Settings raises DSP Unloaded even when the page is cached");
            frame.Navigate(typeof(SettingsPage));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(ReferenceEquals(page, frame.Content), "Repeated entry reuses the cached settings page");
            Check(selector.SelectedItem == selector.Items[0], "Repeated entry keeps the existing General default");
            Check(ProbeSection.Created.Count == 6, "Repeated entry creates no additional sections");
            Check(page.ViewModel.AppViewModel.RefreshCount == 2, "Every entry still refreshes output devices");
            selector.SelectedItem = selector.Items[4];
            await Settle(frame);
            Check(ProbeSection.Created.Count == 6 && ProbeSection.Created.OfType<LyricsSettingsControl>().Single().Loads == 2,
                "Selector binding still updates after cached page reattachment");
            frame.Navigate(typeof(Page));
            await Settle(frame);
            frame.GoBack();
            await Settle(frame);
            Check(ReferenceEquals(page, frame.Content) && selector.SelectedItem == selector.Items[0], "History reentry reuses settings and restores General");
            Check(page.ViewModel.AppViewModel.RefreshCount == 3, "History reentry still refreshes devices");
            frame.GoForward();
            await Settle(frame);
            Check(frame.Content is Page and not SettingsPage, "Forward navigation leaves Settings");
            frame.GoBack();
            await Settle(frame);
            Check(ReferenceEquals(page, frame.Content), "Back after forward still reuses the cached page");
            frame.CacheSize = 0;
            frame.Navigate(typeof(Page));
            await Settle(frame);
            frame.Navigate(typeof(SettingsPage));
            await Settle(frame);
            Check(!ReferenceEquals(page, frame.Content), "Frame may evict the page when its cache budget is disabled");
            Check(ProbeSection.Created.Count == 7 && ProbeSection.Created[^1] is GeneralSettingsControl,
                "After eviction a fresh page still creates only General");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), string.Join("\r\n", _results) + "\r\n");
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), string.Join("\r\n", _results) + "\r\nFAIL: " + error);
            Environment.ExitCode = 1;
        }
        finally { window?.Close(); Exit(); }
    }
    private static async Task Settle(FrameworkElement element)
    {
        element.UpdateLayout();
        await Task.Delay(350);
        element.UpdateLayout();
    }
}
