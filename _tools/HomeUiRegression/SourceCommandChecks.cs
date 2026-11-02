using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View.Controls;

internal sealed partial class RegressionApplication
{
    /// <summary>验证生产列表行的来源命令、参数及本地/远程复用后的可用性。</summary>
    private async Task CheckSourceCommandAsync(Window window)
    {
        var local = new Music { Title = "Local source", Path = "fixture.flac" };
        var remote = new Music { Title = "Remote source", SourceId = 42, Path = "/music/fixture.flac" };
        var row = new MusicListRowControl { Music = local, Width = 1000 };
        window.Content = new Grid { Children = { row } };
        PolishNative.ShowWithoutActivation(window);
        await Wait(() => row.IsLoaded);
        var source = Descendants<Button>(row).Single(button => ReferenceEquals(button.Command, MusicCommands.OpenSourceCommand));
        Check(source.IsEnabled && ReferenceEquals(source.CommandParameter, local), "shipping row binds local source command and song parameter");
        ((IInvokeProvider)new ButtonAutomationPeer(source).GetPattern(PatternInterface.Invoke)).Invoke();
        await Wait(() => ReferenceEquals(MusicCommands.LastOpenedSource, local));
        Check(true, "source button invokes the shared command without launching Explorer in the fixture");

        row.Music = remote;
        await Wait(() => ReferenceEquals(source.CommandParameter, remote) && !source.IsEnabled);
        Check(ReferenceEquals(MusicCommands.LastOpenedSource, local), "recycled remote row disables the source action");
        row.Music = local;
        await Wait(() => ReferenceEquals(source.CommandParameter, local) && source.IsEnabled);
        Check(true, "recycled local row restores source action and parameter");
    }
}
