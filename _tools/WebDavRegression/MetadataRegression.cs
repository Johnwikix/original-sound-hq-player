using WinUIMusicPlayer.Services.WebDav;

internal static class MetadataRegression
{
    public static async Task RunAsync(WebDavTransport transport)
    {
        string path = Path.Combine(Path.GetTempPath(), "remote-tags-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            var frames = new byte[417 * 8];
            for (int i = 0; i < 8; i++) new byte[] { 255, 251, 144, 192 }.CopyTo(frames, i * 417);
            await File.WriteAllBytesAsync(path, frames);
            var tags = new WinUIMusicPlayer.Model.AdditionalMusicMetadata("Album artist", "Jazz", "Composer", "Lyricist", "Conductor", "CAT-001");
            var track = new ATL.Track(path);
            WinUIMusicPlayer.Utils.AudioMetadataTags.Write(track, tags);
            Check(track.Save(), "generated remote tag fixture saves");
            using var input = File.OpenRead(path);
            var metadata = RemoteMetadataProbe.ReadMetadata(input, ".mp3", default);
            var exposed = new WinUIMusicPlayer.Model.AdditionalMusicMetadata("Album artist", "Jazz", "Composer", "", "", "CAT-001");
            Check(metadata.AdditionalMetadata == exposed, "remote probe keeps directly exposed tags and does not guess unavailable role aliases");
        }
        finally { File.Delete(path); }
        foreach (int rate in new[] { 2822400, 5644800, 11289600 })
        {
            using var fixture = new DsfHttpFixture(dsdRate: rate);
            using var reference = new VirtualDsf(rate);
            var connection = new WebDavConnection(new Uri(new Uri(fixture.Url), "/"), "", "");
            var entry = new WebDavEntry("/audio.dsf", "audio.dsf", false, reference.Length, "", null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var input = new HttpRangeReadStream(transport, connection, entry, deadline.Token);
            var metadata = await Task.Run(() => RemoteMetadataProbe.ReadMetadata(input, ".dsf", deadline.Token));
            Console.WriteLine($"DSF {rate}: sample rate={metadata.SampleRate}, bits={metadata.BitDepth}, channels={metadata.Channels}, duration={metadata.DurationMs}, downloaded={input.DownloadedBytes}");
            Check(metadata.SampleRate == rate, $"DSF sample rate: expected {rate}, got {metadata.SampleRate}");
            Check(metadata.BitDepth == 1 && metadata.Channels == 2, "DSF is stereo one-bit audio");
            Check(Math.Abs(metadata.DurationMs - 1024L * 1024 * 1024 * 4 * 1000d / rate) < 1,
                "DSF duration must retain the demuxer's time base");
            Check(input.DownloadedBytes < 512 * 1024, "DSF metadata must use bounded header/tail reads");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
