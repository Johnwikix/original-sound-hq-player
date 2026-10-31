using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Composition;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class HomePolishWindow
{
    private async Task CheckCoverRoundTrips()
    {
        var scroll = (ScrollViewer)_page!.FindName("ContentScroll");
        scroll.ChangeView(null, 550, null, true);
        await Task.Delay(500);
        var items = Descendants<HomeSongItem>((RecommendationShelf)_page.FindName("SongsShelf")).ToArray();
        var observations = new List<object>();
        bool rounded = true;
        foreach (int index in new[] { 1, 2 })
        {
            var item = items[index];
            var cover = Descendants<MusicCoverControl>(item).First();
            var visual = ElementCompositionPreview.GetElementVisual((FrameworkElement?)cover.FindName("CoverPresentation") ?? cover);
            bool before = visual.Clip is CompositionGeometricClip;
            item.Focus(FocusState.Pointer);
            var peer = FrameworkElementAutomationPeer.FromElement(item) ?? FrameworkElementAutomationPeer.CreatePeerForElement(item);
            ((IInvokeProvider)peer!.GetPattern(PatternInterface.Invoke)).Invoke();
            await Wait(() => _home.IsInDetailMode);
            await Task.Delay(950);
            bool entering = visual.Clip is CompositionGeometricClip;
            _page.CollapseDetail();
            await Wait(() => !_home.HasDetails);
            await Task.Delay(600);
            bool returning = visual.Clip is CompositionGeometricClip;
            observations.Add(new { Index = index, Before = before, Entering = entering, Returning = returning,
                Clip = visual.Clip?.GetType().Name, cover.IsLoaded, cover.ActualWidth, cover.ActualHeight });
            rounded &= before && entering && returning;
        }
        File.WriteAllText(Path.Combine(_output, "roundtrip-clips.json"), System.Text.Json.JsonSerializer.Serialize(observations, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await WindowCapture.CaptureAsync(this, "home-polish-roundtrip.png");
        Assert(rounded, "both middle song covers keep rounded clipping after real property entry and return");
    }
}
