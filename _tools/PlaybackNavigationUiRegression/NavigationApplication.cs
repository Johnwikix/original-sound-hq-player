using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer;

internal sealed class NavigationApplication : Application
{
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var output = new StringWriter();
        Console.SetOut(output);
        App.MainWindow = new Window
        {
            Content = new StackPanel
            {
                Children = { new FontIcon { Glyph = "\uEBD3" }, new TextBlock { Text = "WebDAV navigation regression" } }
            }
        };
        App.MainWindow.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
        App.MainWindow.Activate();
        try
        {
            await Regression.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "PASS: real WinUI dispatcher\n" + output);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "result.txt"), "FAIL: " + ex + "\n" + output);
        }
        finally { App.MainWindow.Close(); Exit(); }
    }
}
