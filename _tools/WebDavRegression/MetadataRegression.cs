using WinUIMusicPlayer.Services.WebDav;

internal static class MetadataRegression
{
    public static async Task RunAsync(WebDavTransport transport)
    {
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
