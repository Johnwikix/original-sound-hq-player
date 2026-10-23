using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Net;
using System.Text;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.LastFm;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.View.SubView.Settings;
using WinUIMusicPlayer.ViewModel.Pages;

internal static class Program
{
    [STAThread] private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initialization =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new TestApplication();
        });
    }
}

internal sealed partial class TestApplication : Application, IXamlMetadataProvider
{
    private readonly IXamlMetadataProvider[] _metadata =
    [new Microsoft.UI.Xaml.XamlTypeInfo.XamlControlsXamlMetaDataProvider(),
     new CommunityToolkit.WinUI.Controls.SettingsControlsRns.CommunityToolkit_WinUI_Controls_SettingsControls_XamlTypeInfo.XamlMetaDataProvider(),
     (IXamlMetadataProvider)Activator.CreateInstance(typeof(LastFmSettingsControl).Assembly.GetType(
         "LastFmUiRegression.LastFmUiRegression_XamlTypeInfo.XamlMetaDataProvider", throwOnError: true)!)!];
    public IXamlType GetXamlType(Type type) => _metadata.Select(provider => provider.GetXamlType(type)).FirstOrDefault(value => value is not null)!;
    public IXamlType GetXamlType(string name) => _metadata.Select(provider => provider.GetXamlType(name)).FirstOrDefault(value => value is not null)!;
    public XmlnsDefinition[] GetXmlnsDefinitions() => [];
    private int _checks;
    private void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string resultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
        UnhandledException += (_, error) => File.WriteAllText(resultPath, "FAIL: " + error.Exception);
        Resources.MergedDictionaries.Add(new XamlControlsResources());
        Resources["SettingsCardSpacing"] = 4.0;
        Resources["SettingsSectionHeaderTextBlockStyle"] = new Style(typeof(TextBlock));
        var window = new Window { Title = "Last.fm isolated regression" };
        var lifecycle = new AppLifecycle();
        var vault = new TestVault();
        string path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"), "LastFm.json");
        var store = new LastFmStore(vault, path);
        using var handler = new TestHttp();
        using var api = new LastFmApiClient(new HttpClient(handler), new Uri("https://lastfm.invalid/"));
        var service = new LastFmService(api, store);
        var vm = new LastFmSettingsViewModel(service, lifecycle);
        var state = new AppState();
        var coordinator = new PlaybackCoordinator();
        var remote = new RemotePlaybackService();
        var ipc = new IpcService();
        var bridge = new LastFmPlaybackBridge(state, coordinator, remote, ipc, service, NullLogger<LastFmPlaybackBridge>.Instance);
        try
        {
            WinUIMusicPlayer.App.Services = new ServiceCollection().AddSingleton(vm).BuildServiceProvider();
            vm.Start(DispatcherQueue.GetForCurrentThread());
            bridge.Start(DispatcherQueue.GetForCurrentThread());
            var control = new LastFmSettingsControl();
            window.Content = new ScrollViewer { Content = control };
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(850, 950));
            window.AppWindow.Move(new Windows.Graphics.PointInt32(-30000, -30000));
            window.Activate();
            await service.StartAsync();
            await Task.Delay(150);
            var expander = Descendants(control).OfType<CommunityToolkit.WinUI.Controls.SettingsExpander>().Single();
            expander.IsExpanded = true;
            await Task.Delay(150);
            T Find<T>(string name) where T : FrameworkElement => Descendants(control).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == name);
            var key = Find<TextBox>("LastFmApiKey");
            var secret = Find<PasswordBox>("LastFmSharedSecret");
            var save = Find<Button>("LastFmSaveCredentials");
            var enabled = Find<ToggleSwitch>("LastFmEnabled");
            key.Text = new string('a', 32);
            secret.Password = new string('b', 32);
            Check(vm.ApiKey == key.Text && vm.SharedSecret == secret.Password && save.IsEnabled, "Compiled two-way credential binding or command readiness failed.");
            await vm.SaveCredentialsCommand.ExecuteAsync(null);
            await Task.Delay(100);
            Check(secret.Password.Length == 0 && vm.SecretHint.Contains("保存"), "Saved secret remained in the view or save status was stale.");
            await service.BeginAuthorizationAsync();
            await Task.Delay(100);
            Check(Find<Button>("LastFmCompleteAuthorization").IsEnabled, "Late authorization status did not enable the completion command.");
            await vm.CompleteAuthorizationCommand.ExecuteAsync(null);
            await Task.Delay(100);
            Check(enabled.IsOn && enabled.IsEnabled && vm.AccountText.Contains("alice"), "Authorization did not update compiled account/toggle bindings.");

            var music = new Music();
            state.Playback.CurrentPlayingMusic = music;
            coordinator.Select(music);
            state.Playback.IsPlaying = true;
            Set(0, 0);
            bridge.Sample();
            await Task.Delay(30);
            Check(handler.NowPlaying == 0, "Unconfirmed selection published Now Playing.");
            coordinator.Confirm(music, ipc.Progress);
            await WaitFor(() => handler.NowPlaying >= 1);
            for (int second = 1; second <= 40; second++) { Set(second * 1000, second * 1000); bridge.Sample(); }
            // Seek jumps and long pause are represented with the same asynchronous raw snapshot boundaries as IPC.
            Set(150000, 41000, seek: 1, epoch: 2); bridge.Sample();
            state.Playback.IsPlaying = false;
            Set(150000, 141000, false, seek: 1, epoch: 2); bridge.Sample();
            state.Playback.IsPlaying = true;
            for (int second = 1; second <= 40; second++) { Set(150000 + second * 1000, 141000 + second * 1000, seek: 1, epoch: 2); bridge.Sample(); }
            await Task.Delay(80);
            Check(handler.Scrobbles == 0, "Seek or pause caused premature scrobbling through the real bridge.");
            for (int second = 41; second <= 52; second++) { Set(150000 + second * 1000, 141000 + second * 1000, seek: 1, epoch: 2); bridge.Sample(); }
            await WaitFor(() => handler.Scrobbles == 1);
            Check(handler.NowPlaying >= 1, "Confirmed local playback did not publish Now Playing.");
            for (int second = 53; second <= 100; second++) { Set(150000 + second * 1000, 141000 + second * 1000, seek: 1, epoch: 2); bridge.Sample(); }
            await Task.Delay(80);
            Check(handler.Scrobbles == 1, "One occurrence was scrobbled more than once.");

            // Same Music object can start a new occurrence through gapless repeat.
            coordinator.Select(music);
            Set(0, 300000, epoch: 3, gapless: 1);
            coordinator.Confirm(music, ipc.Progress);
            for (int second = 1; second <= 90; second++) { Set(second * 1000, 300000 + second * 1000, epoch: 3, gapless: 1); bridge.Sample(); }
            await WaitFor(() => handler.Scrobbles == 2);
            Check(handler.Scrobbles == 2, "Gapless repeat of the same object did not create a fresh occurrence.");

            music = new Music { IsRemote = true };
            state.Playback.CurrentPlayingMusic = music;
            coordinator.Select(music);
            state.Playback.IsPlaying = false;
            Set(0, 400000, false, epoch: -1);
            remote.Confirm(music, ipc.Progress with { Playing = true });
            for (int second = 1; second <= 120; second++) { Set(0, 400000 + second * 1000, false, epoch: -1); bridge.Sample(); }
            Check(handler.Scrobbles == 2, "WebDAV buffering was scrobbled.");
            state.Playback.IsPlaying = true;
            for (int second = 1; second <= 45; second++) { Set(second * 1000, 520000 + second * 1000, epoch: -1); bridge.Sample(); }
            coordinator.Select(music);
            Set(45000, 600000, epoch: -2);
            remote.Confirm(music, ipc.Progress, resumed: true);
            for (int second = 1; second <= 48; second++) { Set(45000 + second * 1000, 600000 + second * 1000, epoch: -2); bridge.Sample(); }
            await WaitFor(() => handler.Scrobbles == 3);
            Check(handler.Scrobbles == 3, "Interrupted WebDAV resume lost the original listening occurrence.");

            enabled.IsOn = false;
            await WaitFor(() => !service.Status.Enabled && !vm.IsBusy);
            Check(!enabled.IsOn, "Toggle did not persist through MVVM.");
            enabled.IsOn = true;
            await WaitFor(() => service.Status.Enabled && !vm.IsBusy);
            for (int second = 49; second <= 150; second++) { Set(45000 + second * 1000, 600000 + second * 1000, epoch: -2); bridge.Sample(); }
            await Task.Delay(80);
            Check(handler.Scrobbles == 3, "Disabling and re-enabling duplicated an already queued occurrence.");
            await vm.DisconnectCommand.ExecuteAsync(null);
            await Task.Delay(100);
            Check(!enabled.IsOn && !enabled.IsEnabled && !vm.AccountText.Contains("alice"), "Disconnect left stale account state or an enabled toggle.");
            Check(vm.SharedSecret.Length == 0 && service.Status.PendingCount == 0, "Disconnect retained a secret draft or pending plays.");
            control.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(80);
            control.RequestedTheme = ElementTheme.Light;
            await Task.Delay(80);
            File.WriteAllText(resultPath, $"PASS: {_checks} real WinUI checks; production XAML/compiled bindings, background status dispatch, commands, local/seek/pause/gapless/WebDAV bridge, account changes, disable and light/dark themes.");

            void Set(int position, int timestamp, bool playing = true, long seek = 0, long epoch = 1, long gapless = 0)
                => ipc.Progress = new(timestamp, epoch, position, 180000, (long)(timestamp / 1000.0 * Stopwatch.Frequency), playing, seek, GaplessToken: gapless);
        }
        catch (Exception error) { File.WriteAllText(resultPath, "FAIL: " + error); }
        finally
        {
            await bridge.StopAsync();
            await service.StopAsync();
            await vm.StopAsync();
            window.Close();
            Exit();
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Expected UI/service state did not arrive.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }
}

internal sealed class TestVault : ILastFmCredentialVault
{
    private string? _value;
    public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(_value);
    public Task WriteAsync(string? value, CancellationToken token) { _value = value; return Task.CompletedTask; }
}
internal sealed class TestHttp : HttpMessageHandler
{
    public int Scrobbles, NowPlaying;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string body = await request.Content!.ReadAsStringAsync(token);
        await Task.Delay(10, token);
        string result;
        if (body.Contains("auth.getToken")) result = "{\"token\":\"test-token\"}";
        else if (body.Contains("auth.getSession")) result = "{\"session\":{\"name\":\"alice\",\"key\":\"test-session\"}}";
        else if (body.Contains("track.updateNowPlaying")) { Interlocked.Increment(ref NowPlaying); result = "{\"nowplaying\":{}}"; }
        else { Interlocked.Increment(ref Scrobbles); result = "{\"scrobbles\":{\"@attr\":{\"accepted\":\"1\"}}}"; }
        return new(HttpStatusCode.OK) { Content = new StringContent(result, Encoding.UTF8, "application/json") };
    }
}
