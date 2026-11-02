using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.Controls;
using WinUIMusicPlayer.ViewModel;

// Only database metadata is substituted; local analysis uses the real service, worker and file sharing.
namespace WinUIMusicPlayer.Services
{
    public sealed record SpectrumProbeRemoteTrack(int SourceId, string Href, long Length, string ETag);
    public partial class MusicDatabaseService
    {
        public Task<SpectrumProbeRemoteTrack?> GetRemoteTrackAsync(int id) => Task.FromResult<SpectrumProbeRemoteTrack?>(null);
    }
    public sealed partial class WebDavLibraryService
    {
        public static WebDavEntry ToEntry(SpectrumProbeRemoteTrack track) => new(track.Href, "", false, track.Length, track.ETag, null);
    }
}
namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed record WebDavEntry(string Href, string Name, bool IsDirectory, long Length, string ETag, DateTimeOffset? Modified);
}

internal sealed partial class RegressionApplication
{
    private sealed record SpectrumReferences(WeakReference Panel, WeakReference View, WeakReference Model, WeakReference ParentModel, WeakReference Editor, WeakReference Lyrics, WeakReference Metadata, WeakReference? Canvas, WeakReference? Bitmap);
    private sealed record SpectrumMemorySample(string Phase, long Managed, long Private, int Handles, ulong Video, int Panels, int Views, int Models, int ParentModels, int Editors, int Lyrics, int Metadata, int Canvases, int Bitmaps, int CacheBytes, string Subscribers);

    private async Task CheckSpectrogramMemoryAsync(Window window, MusicDatabaseService database, AppViewModel app)
    {
        string songPath = Environment.GetEnvironmentVariable("SPECTRUM_MEMORY_SONG")
            ?? throw new InvalidOperationException("Set SPECTRUM_MEMORY_SONG to a local audio file for this integration probe.");
        var songs = Enumerable.Range(0, 4).Select(i => new Music { Id = 8100 + i, Title = "Spectrum memory " + i, Path = songPath, Album = "Spectrum probe" }).ToArray();
        app.Replace(songs.ToList());
        foreach (var song in songs) ToolUtils.PrepareArtwork(song.Id);
        var spectrumService = new SpectrogramService(database, new RemoteAudioCache());
        var factory = new FixtureEditorFactory(database, app) { SpectrumSource = spectrumService };
        var navigation = new LibraryNavigationService();
        using var services = new ServiceCollection().AddSingleton(app).AddSingleton(database).AddSingleton(factory.Sessions)
            .AddSingleton<IMusicDetailsEditorFactory>(factory).AddSingleton(navigation).BuildServiceProvider();
        var previousServices = App.Services;
        App.Services = services;
        var frame = new Frame { CacheSize = 0 };
        window.Content = frame;
        PolishNative.ShowWithoutActivation(window);
        UniversalOriginPage.Songs = songs;
        using var presenter = new MusicDetailsPresenter(frame);
        navigation.Open = (music, cover, title) => presenter.Show(new MusicDetailsRequest(music, typeof(UniversalOriginPage)), cover, title);
        navigation.Close = () => presenter.Back();
        var references = new List<SpectrumReferences>();
        var samples = new List<SpectrumMemorySample>();
        try
        {
            frame.Navigate(typeof(UniversalOriginPage));
            await Wait(() => frame.Content is UniversalOriginPage { IsLoaded: true });
            var origin = (UniversalOriginPage)frame.Content;
            var row = (MusicListRowControl)origin.List.Items[0];
            await Wait(() => row.IsLoaded);
            await Task.Delay(3000); // Allow an external runtime-counter collector to attach.
            samples.Add(Sample("initial", spectrumService, references));
            for (int i = 0; i < 6; i++) references.Add(await SpectrumRoundTripAsync(presenter, row, false));
            await CollectSpectrumProbeAsync();
            samples.Add(Sample("tags-only-6", spectrumService, references));
            for (int i = 0; i < 6; i++) references.Add(await SpectrumRoundTripAsync(presenter, row, true));
            await CollectSpectrumProbeAsync();
            samples.Add(Sample("spectrum-6", spectrumService, references));
            for (int i = 0; i < 6; i++) references.Add(await SpectrumRoundTripAsync(presenter, row, true));
            await CollectSpectrumProbeAsync();
            samples.Add(Sample("spectrum-12", spectrumService, references));
            await Task.Delay(2500);
            samples.Add(Sample("idle", spectrumService, references));
            // Diagnostic only: Trim releases eligible device caches, not live bitmaps. Never dispose the shared device.
            Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice().Trim();
            await CollectSpectrumProbeAsync();
            samples.Add(Sample("device-cache-trimmed", spectrumService, references));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "spectrum-memory.json"), JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            var closed = samples.Single(sample => sample.Phase == "spectrum-12");
            Check(closed.Panels == 0 && closed.Views == 0 && closed.Models == 0 && closed.ParentModels == 0
                && closed.Editors == 0 && closed.Lyrics == 0 && closed.Metadata == 0 && closed.Canvases == 0 && closed.Bitmaps == 0,
                "list-title spectrum round trips release panels, view models and graphics wrappers after collection");
        }
        finally
        {
            presenter.Dismiss();
            await factory.Sessions.StopAsync();
            App.Services = previousServices;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<SpectrumReferences> SpectrumRoundTripAsync(MusicDetailsPresenter presenter, MusicListRowControl row, bool spectrum)
    {
        var title = (Button)row.FindName("MusicTitleButton");
        ((IInvokeProvider)new ButtonAutomationPeer(title).GetPattern(PatternInterface.Invoke)).Invoke();
        await Wait(() => presenter.CurrentDetails?.ViewModel is { Editor.IsLoading: false });
        var panel = presenter.CurrentDetails!;
        var details = (MusicDetailControl)panel.FindName("DetailView");
        await Wait(() => details.IsLoaded);
        object? canvas = null, bitmap = null;
        SpectrogramControl? view = null;
        if (spectrum)
        {
            ((Pivot)details.FindName("WorkspaceTabs")).SelectedIndex = 3;
            await Wait(() => panel.ViewModel!.Spectrogram.Data is not null || !panel.ViewModel.Spectrogram.IsBusy);
            if (panel.ViewModel!.Spectrogram.Data is null) throw new Exception(panel.ViewModel.Spectrogram.Status);
            await Wait(() => Descendants<SpectrogramControl>(details).Any());
            view = Descendants<SpectrogramControl>(details).Single();
            await Wait(() => Field<object?>(view, "_bitmap") is not null);
            canvas = Field<object?>(view, "_canvas");
            bitmap = Field<object?>(view, "_bitmap");
        }
        var model = panel.ViewModel!;
        var references = new SpectrumReferences(new(panel), new(view ?? (object)details), new(model.Spectrogram), new(model), new(model.Editor), new(model.Editor.Lyrics), new(model.Editor.Metadata),
            canvas is null ? null : new(canvas), bitmap is null ? null : new(bitmap));
        presenter.Back();
        await Wait(() => !presenter.IsOpen);
        await model.StopAsync();
        await Task.Delay(80);
        if (view is not null && (Field<object?>(view, "_canvas") is not null || Field<object?>(view, "_bitmap") is not null))
            throw new Exception("Unloaded spectrum retains owned graphics resources.");
        if (view?.ViewModel is not null) throw new Exception("Closed spectrum still has a view-model binding.");
        if (bitmap is Microsoft.Graphics.Canvas.CanvasBitmap ownedBitmap)
        {
            bool closed = false;
            try { ownedBitmap.GetPixelBytes(0, 0, 1, 1); }
            catch (Exception exception) when (exception is ObjectDisposedException || unchecked((uint)exception.HResult) == 0x80000013) { closed = true; }
            if (!closed) throw new Exception("Unloaded spectrum bitmap is still usable instead of disposed.");
        }
        return references;
    }
    private static async Task CollectSpectrumProbeAsync()
    {
        // Probe only: separate live references from deferred GC and native allocator high-water marks.
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(180);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
    private static SpectrumMemorySample Sample(string phase, SpectrogramService service, List<SpectrumReferences> references)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new(phase, GC.GetTotalMemory(false), process.PrivateMemorySize64, process.HandleCount,
            HomePolish.Diagnostics.VideoMemory.Read().Aggregate(0UL, (total, adapter) => total + adapter.LocalUsage + adapter.NonLocalUsage),
            references.Count(r => r.Panel.IsAlive), references.Count(r => r.View.IsAlive), references.Count(r => r.Model.IsAlive), references.Count(r => r.ParentModel.IsAlive),
            references.Count(r => r.Editor.IsAlive), references.Count(r => r.Lyrics.IsAlive), references.Count(r => r.Metadata.IsAlive),
            references.Count(r => r.Canvas?.IsAlive == true), references.Count(r => r.Bitmap?.IsAlive == true),
            (int)typeof(SpectrogramService).GetField("_bytes", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!,
            Subscribers(references.FirstOrDefault()?.Model.Target));
    }
    private static string Subscribers(object? model)
    {
        var events = typeof(CommunityToolkit.Mvvm.ComponentModel.ObservableObject).GetField("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        return model is null ? "collected" : events?.GetValue(model) is MulticastDelegate handlers
            ? string.Join(", ", handlers.GetInvocationList().Select(handler => handler.Target?.GetType().FullName + "." + handler.Method.Name)) : "no subscribers";
    }
}
