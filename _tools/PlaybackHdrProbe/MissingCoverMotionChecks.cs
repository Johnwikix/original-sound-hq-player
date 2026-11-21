using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Constants;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

namespace PlaybackHdrProbe;

public sealed partial class ProbeApp
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_outgoingArtwork")]
    private static extern ref PlaybackSceneCover? GetOutgoingArtwork(PlaybackSceneCoverPresenter presenter);

    private static async Task CheckMissingCoverMotionAsync(Window window)
    {
        using var artwork = new TempPng("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGP4z8AAAAMBAQDJ/pLvAAAAAElFTkSuQmCC");
        App.MainWindow = window;
        window.AppWindow.MoveAndResize(new RectInt32(-30000, -30000, 830, 620));
        window.Activate();
        var failures = new List<string>();
        await CheckAsync(shader: false, hdr: false, "XAML cover");
        await CheckAsync(shader: true, hdr: false, "SDR scene cover");
        await CheckAsync(shader: true, hdr: true, "HDR scene cover");
        if (failures.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

        async Task CheckAsync(bool shader, bool hdr, string label)
        {
            var app = new AppViewModel
            {
                DisplayedPlaybackMusic = new object(),
                LyricPageBackgroundHash = "",
                IsFluidBackgroundEnabled = shader,
                PlaybackHdrEnabled = hdr,
                PlaybackHdrWhiteNits = 200,
                PlaybackHdrEffectivePeakNits = 600
            };
            using var page = new PlayingDetailPage(app);
            window.Content = page;
            try
            {
                var current = (ShadowImage)page.Cover.FindName("AlbumArtImage");
                var previous = (ShadowImage)page.Cover.FindName("LastAlbumArtImage");
                var presenter = page.SceneCoverPresenter;
                await WaitUntilAsync(() => shader
                    ? page.Cover.Opacity == 0 && app.PlaybackHdrActive == hdr
                    : current.Source is not null, $"{label}: initial placeholder");
                if (previous.Source is not null || Outgoing() is not null)
                    throw new InvalidOperationException($"{label}: the initial placeholder started a transition.");

                await ChangeCoverAsync(artwork.Path, expectMotion: true, "placeholder → artwork");
                await ChangeCoverAsync(null, expectMotion: true, "artwork → placeholder");
                await ChangeCoverAsync(artwork.Path + ".missing", expectMotion: false, "placeholder → placeholder");

                PlaybackSceneCover? Outgoing() => Volatile.Read(ref GetOutgoingArtwork(presenter));
                object? Presented() => shader ? presenter.RequestedArtwork : current.Source;
                object? TransitionSource() => shader ? Outgoing() : previous.Source;

                async Task ChangeCoverAsync(string? hash, bool expectMotion, string direction)
                {
                    object? before = Presented();
                    app.DisplayedPlaybackMusic = new object();
                    app.LyricPageBackgroundHash = hash;
                    await WaitUntilAsync(() => Presented() is { } source && !ReferenceEquals(source, before),
                        $"{label}: {direction} replaced the cover");
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    bool animated = ReferenceEquals(TransitionSource(), before);
                    while (!animated && timer.Elapsed < Time.AnimationDuration + TimeSpan.FromMilliseconds(100))
                    {
                        await Task.Delay(15);
                        animated = ReferenceEquals(TransitionSource(), before);
                    }
                    if (animated == expectMotion)
                        Console.WriteLine($"PASS {label}: {direction} {(animated ? "animates" : "reuses the placeholder appearance")}.");
                    else
                        failures.Add($"{label}: {direction} {(animated ? "unexpectedly animated" : "skipped the transition")}.");
                    await WaitUntilAsync(() => TransitionSource() is null, $"{label}: {direction} released the outgoing cover");
                    if (shader && page.Cover.Opacity != 0)
                        throw new InvalidOperationException($"{label}: {direction} exposed the XAML preview.");
                }
            }
            finally { window.Content = new Grid(); }
        }
    }
}
