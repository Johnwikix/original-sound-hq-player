using System.Xml.Linq;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinUIMusicPlayer.View;

internal sealed partial class TestWindow
{
    private async Task CheckTabPresenter()
    {
        CheckProductionPresenterXaml();
        var browser = _shell.CurrentLibraryPage!;
        var presenter = (SwitchPresenter)browser.FindName("TabPresenter");
        Check(presenter.ContentTransitions is [PopupThemeTransition], "real WinUI presenter uses one Popup transition");

        // Use a fresh browser so the action can also run after another action opened all tabs.
        var replacement = new MusicBrowsePage();
        var replacementPresenter = (SwitchPresenter)replacement.FindName("TabPresenter");
        Check(replacementPresenter.SwitchCases.OfType<Case>().All(c => c.Content is null), "building the outer browser creates no tab pages");
        browser.SelectBarItem("song");
        _shell.ShellFrame.Content = replacement;
        await Wait(() => ActiveTab is SongListPage { IsLoaded: true });
        Check(replacementPresenter.SwitchCases.OfType<Case>().Count(c => c.Content is Page) == 1, "opening a fresh library creates only the chosen song tab");
        var seen = new Dictionary<string, Page>();
        foreach (string tag in new[] { "song", "album", "artist", "favourite" })
        {
            Page page = await Open(tag);
            seen.Add(tag, page);
            int unloads = 0;
            void CountUnload(object sender, RoutedEventArgs args) => unloads++;
            page.Unloaded += CountUnload;
            int refreshes = _app.RefreshCount;
            try
            {
                replacement.SelectBarItem(tag);
                await Task.Delay(100);
                Check(ReferenceEquals(ActiveTab, page) && unloads == 0 && _app.RefreshCount == refreshes,
                    tag + " repeat click neither detaches the active page nor refreshes its source");
                Check(replacementPresenter.SwitchCases.OfType<Case>().Count(c => c.Content is Page) == seen.Count, tag + " creates no unopened tabs");
            }
            finally { page.Unloaded -= CountUnload; }
        }

        var focusedSelector = Selector.SelectedItem!;
        Check(focusedSelector.Focus(FocusState.Programmatic), "background test can focus a tab selector item");
        foreach (string tag in new[] { "song", "album", "artist", "favourite", "artist", "song" })
            replacement.SelectBarItem(tag);
        await Wait(() => ActiveTab is SongListPage { IsLoaded: true });
        Check(ReferenceEquals(seen["song"], ActiveTab) && Equals(Selector.SelectedItem?.Tag, "song"),
            "rapid switching preserves the final tab and original page identity");
        await Wait(() => seen.Where(pair => pair.Key != "song").All(pair => !pair.Value.IsLoaded &&
            VisualTreeHelper.GetParent(pair.Value) is null));
        Check(seen.Where(pair => pair.Key != "song").All(pair => !pair.Value.IsLoaded &&
            VisualTreeHelper.GetParent(pair.Value) is null), "rapid switching detaches every inactive tab from the visual tree");
        Check(FocusManager.GetFocusedElement(Selector.XamlRoot) is SelectorBarItem,
            "switching tab content does not move focus out of the selector");
        foreach (var (tag, page) in seen)
            Check(ReferenceEquals(page, await Open(tag)), tag + " reuses the first page instance");

        _shell.ShellFrame.Content = browser;
        await Wait(() => browser.IsLoaded && ActiveTab is FavouritePlayListPage { IsLoaded: true });
        Check(ReferenceEquals(_shell.CurrentLibraryPage, browser) && !ReferenceEquals(ActiveTab, seen["favourite"]),
            "returning to a different outer browser activates its own selected tab");
        _shell.NavigateToFoldersPage();
        await Task.Delay(150);
        Check(browser.ActiveTabPage is null && seen.Values.All(page => !page.IsLoaded), "leaving the library leaves no active tab on either browser");
        int refreshesBeforeReentry = _app.RefreshCount;
        _shell.NavigateToMusicBrowsePage();
        await Wait(() => _shell.CurrentLibraryPage?.IsLoaded == true && ActiveTab is FavouritePlayListPage { IsLoaded: true });
        Check(ReferenceEquals(_shell.CurrentLibraryPage, browser), "sidebar return reuses the cached outer browser");
        Check(_app.RefreshCount > refreshesBeforeReentry, "sidebar reentry refreshes the active source");
    }

    private void CheckProductionPresenterXaml()
    {
        using var stream = typeof(TestWindow).Assembly.GetManifestResourceStream("ProductionMusicBrowsePage.xaml")!;
        var production = XDocument.Load(stream);
        var presenter = production.Descendants().Single(node => node.Name.LocalName == "SwitchPresenter");
        var transitions = presenter.Elements().Single(node => node.Name.LocalName == "SwitchPresenter.ContentTransitions");
        Check(transitions.Descendants().Count(node => node.Name.LocalName == nameof(PopupThemeTransition)) == 1,
            "production library XAML declares the Popup transition");
        Check(!production.Descendants().Any(node => node.Name.LocalName is "Frame" or "EntranceNavigationTransitionInfo" or "EntranceThemeTransition"),
            "production library XAML has no nested Frame or entrance transition");
    }

    private async Task CheckEmptyAndFavourite()
    {
        var browser = _shell.CurrentLibraryPage!;
        var placeholder = (Grid)browser.FindName("EmptyLibraryGrid");
        try
        {
            _app.LibraryEmptyVisibility = Visibility.Visible;
            await Open("song");
            await Wait(() => placeholder.Visibility == Visibility.Visible);
            await Open("favourite");
            await Wait(() => placeholder.Visibility == Visibility.Collapsed);
            Check(_app.FavoriteSongs.Count > 0, "favorites override the empty-library prompt when populated");
            await _app.FavoriteSongs.ReplaceAllAsync([]);
            await Wait(() => placeholder.Visibility == Visibility.Visible);
            Check(ActiveTab is FavouritePlayListPage, "clearing favorites shows the empty prompt without replacing the active page");
            await _app.FavoriteSongs.ReplaceAllAsync(_all);
            await Wait(() => placeholder.Visibility == Visibility.Collapsed);
        }
        finally
        {
            _app.LibraryEmptyVisibility = Visibility.Collapsed;
            await _app.FavoriteSongs.ReplaceAllAsync(_all);
        }
    }
}