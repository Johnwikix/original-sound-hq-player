using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using WinUIMusicPlayer.Services.WebDav;

internal static class Regression
{
    public static async Task RunAsync(WebDavTransport transport)
    {
        CheckCoverStream();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        await using var fixture = new Fixture();
        var fake = new WebDavConnection(new Uri(fixture.Root), "fixture-user", "fixture-password");
        var item = new WebDavEntry("/dav/tone.flac", "tone.flac", false, fixture.Bytes.Length, "\"v1\"", null);
        string temp = Path.Combine(Path.GetTempPath(), "webdav-regression-" + Guid.NewGuid().ToString("N"));
        var cache = new RemoteAudioCache();
        cache.Configure(false, temp, 32L * 1024 * 1024);
        var listing = new List<WebDavEntry>();
        await foreach (var entry in transport.ListAsync(fake, "/dav/", default)) listing.Add(entry);
        Check(listing.Count == 2 && listing[0].Href.EndsWith('/') && listing[0].Name == "音乐", "DAV Unicode and collection normalization");
        try { WebDavTransport.Resolve(fake, "/outside/file"); throw new Exception("scope accepted"); }
        catch (WebDavException) { Check(true, "source scope enforcement"); }
        await using (var redirect = await transport.OpenAsync(fake, "/dav/redirect", 0, 9, true, default))
            Check(fixture.UnauthorizedRedirects == 0, "redirect strips credentials outside source root");
        using (var input = new HttpRangeReadStream(transport, fake, item, default, 128 * 1024, 1))
        {
            byte[] bytes = new byte[100];
            input.ReadExactly(bytes);
            Check(bytes.AsSpan().SequenceEqual(fixture.Bytes.AsSpan(0, 100)), "bounded range returns original bytes");
            input.Position = 128 * 1024;
            try { input.ReadByte(); throw new Exception("budget accepted"); }
            catch (WebDavException ex) { Check(ex.Code == "ReadBudgetExceeded" && input.DownloadedBytes == 128 * 1024, "read budget stops before additional I/O"); }
        }
        using (var input = new HttpRangeReadStream(transport, fake, item with { Href = "/dav/changed" }, default))
        {
            try { input.ReadByte(); throw new Exception("version accepted"); }
            catch (WebDavException ex) { Check(ex.Code == "ResourceChanged", "changed strong ETag rejected"); }
        }
        using (var cancel = new CancellationTokenSource(150))
        using (var input = new HttpRangeReadStream(transport, fake, item with { Href = "/dav/stall" }, cancel.Token))
        {
            var clock = Stopwatch.StartNew();
            try { await Task.Run(() => input.ReadByte()); throw new Exception("cancellation ignored"); }
            catch (OperationCanceledException) { Check(clock.Elapsed < TimeSpan.FromSeconds(3), "cancellation interrupts blocked body read"); }
        }
        await using (var bridge = new WebDavPlaybackBridge(new RemoteReadSession(transport, fake, item, cache, "fixture-resource")))
        {
            bridge.Start();
            using var request = new HttpRequestMessage(HttpMethod.Get, bridge.Location);
            request.Headers.Range = new RangeHeaderValue(1048530, 1048690);
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(fixture.Bytes.AsSpan(1048530, 161)), "bridge crosses window boundaries without corruption");
            using var invalid = await http.GetAsync(new Uri(new Uri(bridge.Location), "/wrong"));
            Check(invalid.StatusCode == HttpStatusCode.NotFound, "bridge rejects unknown resource token");
            cache.Configure(true, temp, 32L * 1024 * 1024);
            bridge.SetPlaying(true);
            Check((await http.GetByteArrayAsync(bridge.Location)).AsSpan().SequenceEqual(fixture.Bytes), "cache toggled on during playback preserves bytes");
            await Task.Delay(400);
        }
        int before = fixture.Gets;
        await using (var cached = new WebDavPlaybackBridge(new RemoteReadSession(transport, fake, item, cache, "fixture-resource")))
        {
            cached.Start();
            Check((await http.GetByteArrayAsync(cached.Location)).AsSpan().SequenceEqual(fixture.Bytes) && fixture.Gets == before, "complete cache replays without origin requests");
            cache.Clear();
            Check((await http.GetByteArrayAsync(cached.Location)).Length == fixture.Bytes.Length, "clear preserves active reader lease");
        }
        Check(cache.GetSize() == 0, "clear deletes active cache after final reader closes");
        cache.Configure(false, temp, 32L * 1024 * 1024);
        await using (var sequential = new WebDavPlaybackBridge(new RemoteReadSession(transport, fake, item with { Href = "/dav/no-range" }, cache, "sequential")))
        {
            sequential.Start();
            Check((await http.GetByteArrayAsync(sequential.Location)).AsSpan().SequenceEqual(fixture.Bytes), "no Range server uses bounded sequential streaming");
        }
        Check(!Directory.EnumerateFiles(Path.Combine(temp, "WebDavAudio"), "*.part").Any(), "session cleanup leaves no partial files");
        cache.Configure(true, temp, 32L * 1024 * 1024);
        var small = item with { Length = 65550 };
        await using (var pending = cache.Acquire("toggle-commit", small)!)
        {
            Check(pending.TryWrite(0, fixture.Bytes.AsSpan(0, 65536)), "cache accepts bounded write");
            cache.Configure(false, temp, 32L * 1024 * 1024);
            Check(!pending.TryWrite(65536, fixture.Bytes.AsSpan(65536, 14)), "disabled cache rejects new writes");
            cache.Configure(true, temp, 32L * 1024 * 1024);
            Check(pending.TryWrite(65536, fixture.Bytes.AsSpan(65536, 14)), "reenabling cache fills missing range");
            cache.Configure(false, temp, 32L * 1024 * 1024);
        }
        await using (var complete = cache.Acquire("toggle-commit", small))
            Check(complete?.IsComplete == true, "turning cache off preserves completed download and allows replay");
        cache.Clear();
        cache.Configure(true, temp, 32L * 1024 * 1024);
        Check(cache.Acquire("weak", small with { ETag = "W/\"v1\"" }) is null, "weak version does not create persistent range cache");
    }

    private static void CheckCoverStream()
    {
        byte[] picture = [0xff, 0xd8, 0xff];
        using var block = new MemoryStream();
        void BigEndian(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            block.Write(bytes);
        }
        BigEndian(3);
        BigEndian(10);
        block.Write("image/jpeg"u8);
        BigEndian(0);
        BigEndian(1); BigEndian(1); BigEndian(24); BigEndian(0);
        BigEndian(picture.Length);
        block.Write(picture);
        using var packet = new MemoryStream();
        using (var writer = new BinaryWriter(packet, System.Text.Encoding.UTF8, true))
        {
            writer.Write("OpusTags"u8);
            writer.Write(180);
            writer.Write(new byte[180]);
            writer.Write(1);
            byte[] comment = System.Text.Encoding.ASCII.GetBytes("METADATA_BLOCK_PICTURE=" + Convert.ToBase64String(block.ToArray()));
            writer.Write(comment.Length);
            writer.Write(comment);
        }
        using var ogg = new MemoryStream();
        void Page(byte flags, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[27];
            header.Clear();
            "OggS"u8.CopyTo(header);
            header[5] = flags;
            header[26] = 1;
            ogg.Write(header);
            ogg.WriteByte((byte)data.Length);
            ogg.Write(data);
        }
        Page(2, "OpusHead"u8);
        byte[] commentPacket = packet.ToArray();
        Page(0, commentPacket.AsSpan(0, 255));
        Page(1, commentPacket.AsSpan(255));
        ogg.Position = 0;
        Check(WinUIMusicPlayer.Reader.AudioCoverReader.ReadCover(ogg, ".opus").AsSpan().SequenceEqual(picture), "custom cover parses cross-page Opus tags and leaves stream open");
        ogg.Position = 0;
        Check(WinUIMusicPlayer.Reader.AudioCoverReader.ReadCover(ogg, ".opus", 64).Length == 0 && ogg.CanRead, "cover packet budget rejects oversized packet without closing caller stream");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
