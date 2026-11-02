using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Windows.Foundation;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

internal sealed partial class RegressionApplication
{
    /// <summary>使用真实 XAML 与 ListView 容器检查本地化表头、滚动和响应式布局。</summary>
    private async Task CheckAlbumTrackLayoutAsync(Window window, [CallerFilePath] string source = "")
    {
        var details = new AlbumDetailControl();
        window.Content = details;
        PolishNative.ShowWithoutActivation(window);
        await Wait(() => details.IsLoaded);
        var tabs = (Pivot)details.FindName("WorkspaceTabs");
        tabs.SelectedIndex = 1;
        var list = (ListView)details.FindName("TrackList");
        var header = (Grid)details.FindName("TrackHeader");
        var headings = header.Children.OfType<TextBlock>().ToArray();
        string resourceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../Strings"));
        string[] resourceKeys = ["TrackNumberBox.Text", "TitleTextBlock.Text", "AuthorTextBlock.Text", "DurationTextBlock.Text"];
        var rows = Enumerable.Range(1, 80).Select(i => new AlbumTrackItem(i.ToString(),
            "A long track title / 很长的歌曲标题 " + i, "Artist / 艺术家", "05:47")).ToArray();
        var failures = new List<string>();

        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        foreach (var size in new[] { (900d, 600d), (640d, 540d), (1200d, 760d) })
        {
            // 显式设置 DIP，避免测试结果随当前显示器缩放变化。
            details.Width = size.Item1;
            details.Height = size.Item2;
            details.RequestedTheme = theme;
            foreach (string resourceFile in Directory.GetFiles(resourceRoot, "Resources.resw", SearchOption.AllDirectories))
            {
                var strings = XDocument.Load(resourceFile).Root!.Elements("data")
                    .Where(element => resourceKeys.Contains(element.Attribute("name")!.Value))
                    .ToDictionary(element => element.Attribute("name")!.Value, element => element.Element("value")!.Value);
                string locale = Path.GetFileName(Path.GetDirectoryName(resourceFile))!;
                for (int i = 0; i < headings.Length; i++) headings[i].Text = strings[resourceKeys[i]];
                foreach (int count in new[] { 1, 80 })
                {
                    list.ItemsSource = rows.Take(count).ToArray();
                    list.ScrollIntoView(list.Items[0]);
                    details.UpdateLayout();
                    await Task.Delay(50);
                    details.UpdateLayout();
                    var container = (ListViewItem?)list.ContainerFromIndex(0);
                    if (container is null) throw new Exception("Album track container was not realized");
                    var cells = Descendants<Grid>(container).First(grid => grid.ColumnDefinitions.Count == 4)
                        .Children.OfType<TextBlock>().ToArray();
                    string scenario = $"{theme} {size} {locale} {count} tracks";
                    for (int i = 0; i < headings.Length; i++)
                    {
                        var heading = headings[i];
                        Rect headerSlot = LayoutInformation.GetLayoutSlot(heading);
                        Rect cellSlot = LayoutInformation.GetLayoutSlot(cells[i]);
                        var probe = new TextBlock { Text = heading.Text, FontSize = heading.FontSize,
                            FontFamily = heading.FontFamily, FontWeight = heading.FontWeight, TextWrapping = heading.TextWrapping };
                        probe.Measure(new Size(headerSlot.Width, double.PositiveInfinity));
                        var unwrapped = new TextBlock { Text = heading.Text, FontSize = heading.FontSize, FontFamily = heading.FontFamily };
                        unwrapped.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        if (headerSlot.Height + .5 < probe.DesiredSize.Height || heading.IsTextTrimmed
                            || (heading.TextWrapping == TextWrapping.NoWrap && unwrapped.DesiredSize.Width > headerSlot.Width + .5))
                            failures.Add($"clipped header {i}: {scenario}");
                        double headerX = header.TransformToVisual(details).TransformPoint(new Point(headerSlot.X, 0)).X;
                        var rowGrid = (Grid)cells[i].Parent;
                        double cellX = rowGrid.TransformToVisual(details).TransformPoint(new Point(cellSlot.X, 0)).X;
                        if (Math.Abs(headerX - cellX) > 1 || Math.Abs(headerSlot.Width - cellSlot.Width) > 1)
                            failures.Add($"column {i} header {headerX:F1}/{headerSlot.Width:F1}, row {cellX:F1}/{cellSlot.Width:F1}: {scenario}");
                    }
                }
            }
        }

        // 长列表末尾仍能滚动到，空列表不遗留已实现的曲目。
        list.ScrollIntoView(list.Items[^1]);
        await Task.Delay(80);
        Check(list.ContainerFromIndex(79) is ListViewItem, "album track list scrolls to its last row");
        list.ItemsSource = Array.Empty<AlbumTrackItem>();
        details.UpdateLayout();
        Check(list.Items.Count == 0, "empty album track list clears its rows");
        if (failures.Count > 0) throw new Exception(string.Join(Environment.NewLine, failures));
        Check(true, "album headers fit every translation and align with single/scrolling rows in light/dark at 640/900/1200 DIP");
        details.Width = 900;
        details.Height = 600;
        details.RequestedTheme = ElementTheme.Dark;
        foreach (var (heading, text) in headings.Zip(new[] { "轨道编号", "标题", "艺术家", "时长" })) heading.Text = text;
        list.ItemsSource = rows.Take(1).ToArray();
        details.UpdateLayout();
        await Task.Delay(100);
        await Capture(details, "album-tracks-fixed.png");
    }
}
