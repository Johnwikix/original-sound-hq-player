using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media.Animation;
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
    private readonly IXamlMetadataProvider _local = (IXamlMetadataProvider)Activator.CreateInstance(Type.GetType(
        "SettingsNavigationUiRegression.SettingsNavigationUiRegression_XamlTypeInfo.XamlMetaDataProvider", throwOnError: true)!)!;
    public IXamlType GetXamlType(Type type) => _local.GetXamlType(type) ?? _native.GetXamlType(type);
    public IXamlType GetXamlType(string name) => _local.GetXamlType(name) ?? _native.GetXamlType(name);
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private readonly List<string> _results = [];
    private void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        _results.Add("PASS: " + name);
    }

    private void CheckSelection(SelectorBar selector, int expectedIndex, string description)
    {
        var selectedTags = string.Join(", ", selector.Items.Where(item => item.IsSelected).Select(item => item.Tag));
        var expected = selector.Items[expectedIndex];
        Check(selector.SelectedItem == expected && selectedTags == (string)expected.Tag,
            $"{description} (SelectedItem: {selector.SelectedItem?.Tag}, highlighted: {selectedTags})");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window? window = null;
        try
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            using var provider = new ServiceCollection().AddSingleton<SettingsViewModel>().BuildServiceProvider();
            WinUIMusicPlayer.App.Services = provider;
            var frame = new Frame { CacheSize = 4 };
            window = new Window { Content = frame };
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.AppWindow.Show(false);
            frame.Navigate(typeof(SettingsPage));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(ProbeSection.Created.Count == 1 && ProbeSection.Created[0] is GeneralSettingsControl,
                $"First entry creates only General (actual: {string.Join(", ", ProbeSection.Created.Select(c => c.GetType().Name))})");
            Check(ProbeSection.Created[0].ActualWidth >= frame.ActualWidth - 4, "Deferred content stretches across the page");
            var general = ProbeSection.Created[0];
            general.Expander.IsExpanded = true;
            general.Scroll.ChangeView(null, 300, null, disableAnimation: true);
            await Settle(frame);
            Check(general.Expander.IsExpanded && general.Scroll.VerticalOffset > 0, "General can expand and scroll");
            var page = (SettingsPage)frame.Content;
            var selector = (SelectorBar)page.FindName("SectionSelector");
            CheckSelection(selector, 0, "First entry selects and highlights General");
            for (int i = 1; i < selector.Items.Count; i++)
            {
                selector.SelectedItem = selector.Items[i];
                await Settle(frame);
                Check(ProbeSection.Created.Count == i + 1, "Selecting " + selector.Items[i].Tag + " creates only that section");
                Check(ProbeSection.Created[^1].Loads == 1, "Selected section loads once");
                Check(ProbeSection.Created[^1].ActualWidth >= frame.ActualWidth - 4, "Selected section keeps the existing full width layout");
            }
            var about = ProbeSection.Created.OfType<AboutSettingsControl>().Single();
            Check(about.Expander.IsExpanded, "About opens with its shipped expanded state");
            about.Expander.IsExpanded = false;
            var dsp = ProbeSection.Created.OfType<DspSettingsControl>().Single();
            Check(dsp.Unloads == 1, "Leaving DSP raises Unloaded for session cleanup");
            selector.SelectedItem = selector.Items[2];
            await Settle(frame);
            Check(ProbeSection.Created.Count == 6 && dsp.Loads == 2, "Returning to DSP reuses its view and runs Loaded again");
            Check(selector.Items[2].Focus(FocusState.Pointer), "DSP can receive pointer focus before navigating away");
            frame.Navigate(typeof(Page));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(dsp.Unloads == 2, "Leaving Settings raises DSP Unloaded for session cleanup");
            frame.Navigate(typeof(SettingsPage));
            frame.BackStack.Clear();
            await Settle(frame);
            Check(ReferenceEquals(page, frame.Content), "Repeated entry reuses the settings page");
            var currentPage = (SettingsPage)frame.Content;
            var currentSelector = (SelectorBar)currentPage.FindName("SectionSelector");
            Check(currentPage.NavigationCacheMode == Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled,
                "Settings participates in the bounded Frame cache");
            CheckSelection(currentSelector, 2, "Repeated entry keeps DSP selected and highlighted");
            Check(ProbeSection.Created.Count == 6 && dsp.IsLoaded && !general.IsLoaded && dsp.Loads == 3,
                "Repeated entry reloads the existing DSP section once");
            Check(currentPage.ViewModel.AppViewModel.RefreshCount == 2, "Every entry still refreshes output devices");
            currentSelector.SelectedItem = currentSelector.Items[0];
            await Settle(frame);
            Check(!general.Expander.IsExpanded && general.Scroll.VerticalOffset < 1, "General restores its collapsed state and top scroll position");
            currentSelector.SelectedItem = currentSelector.Items[5];
            await Settle(frame);
            Check(about.IsLoaded && about.Expander.IsExpanded, "Cached About restores its expanded default on the next visit");
            currentSelector.SelectedItem = currentSelector.Items[4];
            await Settle(frame);
            var lyrics = ProbeSection.Created.OfType<LyricsSettingsControl>().Single();
            Check(ProbeSection.Created.Count == 6 && lyrics.IsLoaded,
                "Selector binding reattaches an already visited section");
            Check(currentSelector.Items[4].Focus(FocusState.Keyboard), "Lyrics can receive keyboard focus before navigating away");
            frame.Navigate(typeof(Page));
            await Settle(frame);
            frame.GoBack();
            await Settle(frame);
            var historyPage = (SettingsPage)frame.Content;
            var historySelector = (SelectorBar)historyPage.FindName("SectionSelector");
            Check(ReferenceEquals(currentPage, historyPage), "History reentry keeps the cached settings page");
            CheckSelection(historySelector, 4, "History reentry keeps Lyrics selected and highlighted");
            Check(ProbeSection.Created.Count == 6 && lyrics.IsLoaded && !general.IsLoaded,
                "History reentry restores the existing Lyrics section only");
            Check(historyPage.ViewModel.AppViewModel.RefreshCount == 3, "History reentry still refreshes devices");
            frame.GoForward();
            await Settle(frame);
            Check(frame.Content is Page and not SettingsPage, "Forward navigation leaves Settings");
            frame.GoBack();
            await Settle(frame);
            Check(ReferenceEquals(historyPage, frame.Content), "Back after forward keeps the cached settings page");
            Check(ProbeSection.Created.Count == 6 && lyrics.IsLoaded, "Back after forward reuses Lyrics");
            CheckSelection(historySelector, 4, "Back after forward keeps Lyrics selected and highlighted");

            for (int i = 1; i < historySelector.Items.Count; i++)
            {
                var item = historySelector.Items[i];
                historySelector.SelectedItem = item;
                await Settle(frame);
                Check(item.Focus(FocusState.Pointer), $"{item.Tag} can receive pointer focus");
                frame.Navigate(typeof(Page), null, new EntranceNavigationTransitionInfo());
                frame.BackStack.Clear();
                await Settle(frame);
                frame.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
                frame.BackStack.Clear();
                await Settle(frame);
                Check(ReferenceEquals(historyPage, frame.Content) && ProbeSection.Created.Count == 6 && ProbeSection.Created[i].IsLoaded,
                    $"Returning from {item.Tag} shows the same content on the cached settings page");
                CheckSelection(historySelector, i, $"Returning from {item.Tag} keeps that section selected and highlighted");
            }

            frame.Navigate(typeof(Page));
            frame.BackStack.Clear();
            frame.CacheSize = 0;
            await Settle(frame);
            frame.CacheSize = 4;
            frame.Navigate(typeof(SettingsPage));
            frame.BackStack.Clear();
            await Settle(frame);
            var rebuiltPage = (SettingsPage)frame.Content;
            Check(!ReferenceEquals(historyPage, rebuiltPage), "Eviction creates a new settings page");
            CheckSelection((SelectorBar)rebuiltPage.FindName("SectionSelector"), 5, "Rebuilt page still selects and highlights About");
            Check(ProbeSection.Created.Count == 7 && ProbeSection.Created[^1] is AboutSettingsControl { IsLoaded: true },
                "Rebuilt settings page creates only the remembered section");

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
