using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using WinUIMusicPlayer.Services.WebDav;

internal static class Regression
{
    public static async Task RunAsync(WebDavTransport transport)
    {
        await using (var tlsFixture = new Fixture(tls: true, nameMismatch: true))
        {
            var tlsConnection = new WebDavConnection(new Uri(tlsFixture.Root), "", "");
            WebDavCertificate? observed = null;
            try
            {
                await foreach (var _ in transport.ListAsync(tlsConnection, "/dav/", default)) { }
                throw new Exception("Untrusted TLS certificate was accepted.");
            }
            catch (WebDavCertificateException ex)
            {
                observed = ex.Certificate;
                Check(ex.Code == "CertificateUntrusted" && ex.Status is null && observed.Sha256.Length == 64 && observed.NameMismatch,
                    "untrusted HTTPS certificate reports reviewable details without accepting TLS");
            }
            var trustedConnection = tlsConnection with { CertificateTrust = new(observed!.Origin, observed.Sha256) };
            int count = 0;
            await foreach (var _ in transport.ListAsync(trustedConnection, "/dav/", default)) count++;
            Check(count == 2, "exact confirmed certificate permits HTTPS directory listing");
            await using (var audio = await transport.OpenAsync(trustedConnection, "/dav/tone.flac", 0, 15, true, default))
            {
                var bytes = new byte[16];
                await audio.Stream.ReadExactlyAsync(bytes);
                Check(bytes.AsSpan().SequenceEqual(tlsFixture.Bytes.AsSpan(0, 16)), "confirmed certificate also supports playback Range requests");
            }
            try
            {
                await foreach (var _ in transport.ListAsync(tlsConnection, "/dav/", default)) { }
                throw new Exception("Strict source reused a pinned TLS connection.");
            }
            catch (WebDavCertificateException) { Check(true, "unconfirmed source cannot reuse confirmed source TLS connections"); }
            try
            {
                await foreach (var _ in transport.ListAsync(tlsConnection with { CertificateTrust = new(observed.Origin, new string('0', 64)) }, "/dav/", default)) { }
                throw new Exception("Changed certificate accepted.");
            }
            catch (WebDavCertificateException ex) { Check(ex.Code == "CertificateChanged", "changed certificate requires fresh confirmation"); }
            await using var otherServer = new Fixture(tls: true);
            try
            {
                await foreach (var _ in transport.ListAsync(trustedConnection with { Root = new Uri(otherServer.Root) }, "/dav/", default)) { }
                throw new Exception("Certificate trust escaped its origin.");
            }
            catch (WebDavCertificateException ex) { Check(ex.Code == "CertificateUntrusted", "certificate trust is scoped to source origin including port"); }
        }
        await using (var expiredServer = new Fixture(tls: true, expired: true))
        {
            var expiredConnection = new WebDavConnection(new Uri(expiredServer.Root), "", "");
            WebDavCertificate? expired = null;
            try { await foreach (var _ in transport.ListAsync(expiredConnection, "/dav/", default)) { } }
            catch (WebDavCertificateException ex) { expired = ex.Certificate; }
            Check(expired is { CanTrust: false }, "expired certificate cannot be confirmed");
            try
            {
                await foreach (var _ in transport.ListAsync(expiredConnection with { CertificateTrust = new(expired!.Origin, expired.Sha256) }, "/dav/", default)) { }
                throw new Exception("Expired pinned certificate accepted.");
            }
            catch (WebDavCertificateException) { Check(true, "saved fingerprint does not bypass certificate expiry"); }
        }
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
        Check(cache.HasCompleteFile("fixture-resource", item), "playback availability recognizes exact complete cache");
        Check(!cache.HasCompleteFile("fixture-resource", item with { ETag = "\"v2\"" }), "new source version cannot reuse old cache");
        await using (var cached = new WebDavPlaybackBridge(new RemoteReadSession(transport,
            () => throw new InvalidOperationException("cached playback must not load credentials or connect"), item, cache, "fixture-resource")))
        {
            cached.Start();
            Check((await http.GetByteArrayAsync(cached.Location)).AsSpan().SequenceEqual(fixture.Bytes) && fixture.Gets == before, "complete cache replays without origin requests");
            cache.Clear();
            Check(!cache.HasCompleteFile("fixture-resource", item), "cleared active cache is unavailable to new selections");
            Check((await http.GetByteArrayAsync(cached.Location)).Length == fixture.Bytes.Length, "clear preserves active reader lease");
        }
        Check(cache.GetSize() == 0, "clear deletes active cache after final reader closes");
        cache.Configure(false, temp, 32L * 1024 * 1024);
        await using (var sequential = new WebDavPlaybackBridge(new RemoteReadSession(transport, fake, item with { Href = "/dav/no-range" }, cache, "sequential")))
        {
            sequential.Start();
            Check((await http.GetByteArrayAsync(sequential.Location)).AsSpan().SequenceEqual(fixture.Bytes), "no Range server uses bounded sequential streaming");
        }
        Check(!Directory.EnumerateFiles(WebDavCachePaths.Audio(temp), "*.part").Any(), "session cleanup leaves no partial files");
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
        await CheckCacheDirectoriesAsync(temp);
        var retryClock = new ManualClock();
        var availability = new WebDavAvailability(retryClock);
        long oldProbe = availability.Version(10);
        availability.ReportFailure(10);
        Check(availability.IsOffline(10) && availability.ShouldDefer(10) && !availability.ShouldDefer(20), "cooldown is scoped to failed source");
        Check(!availability.CompleteProbe(10, oldProbe, false) && availability.IsOffline(10), "late probe cannot erase newer streaming failure");
        retryClock.Advance(WebDavAvailability.RetryDelay);
        Check(!availability.ShouldDefer(10) && availability.IsOffline(10), "expiry permits retry without inventing online status");
        Check(availability.CompleteProbe(10, availability.Version(10), false) && !availability.IsOffline(10), "successful new probe recovers source");
        Check(!WebDavReadFailure.From(new WebDavException("RequestFailed", HttpStatusCode.NotFound)).SourceUnavailable,
            "missing track does not take entire server offline");
    }

    private static async Task CheckCacheDirectoriesAsync(string temp)
    {
        string first = Path.Combine(temp, "first");
        string second = Path.Combine(temp, "second");
        var cache = new RemoteAudioCache();
        var entry = new WebDavEntry("/dav/small.flac", "small.flac", false, 1024, "\"v1\"", null);
        int cacheChanges = 0;
        cache.ContentsChanged += () => cacheChanges++;
        byte[] bytes = new byte[1024];
        new Random(31).NextBytes(bytes);
        cache.Configure(true, first, entry.Length);
        var old = cache.Acquire("same-resource", entry)!;
        Check(old.TryWrite(0, bytes), "first directory accepts bounded cache write");
        Check(cache.GetCompleteFiles().Count == 0, "in-flight partial audio does not expose a downloaded icon");
        cache.Configure(true, second, entry.Length);
        Check(!old.CanWrite, "changing cache root invalidates old writer");
        await using (var current = cache.Acquire("same-resource", entry))
            Check(current is not null && current.TryWrite(0, bytes), "old directory reservations do not consume new directory capacity");
        Check(cacheChanges >= 3 && cache.GetCompleteFiles().GetValueOrDefault(RemoteAudioCache.GetKey("same-resource", entry)) == entry.Length,
            "committing complete audio publishes cache state with its exact version and size");
        Check(!cache.GetCompleteFiles().ContainsKey(RemoteAudioCache.GetKey("same-resource", entry with { ETag = "\"v2\"" })),
            "a changed remote version does not inherit the old downloaded icon");
        await old.DisposeAsync();
        Check(!Directory.EnumerateFiles(WebDavCachePaths.Audio(first)).Any(), "old writer cannot publish after directory switch and removes partial file");

        string completePath;
        await using (var complete = cache.Acquire("same-resource", entry))
        {
            Check(complete?.IsComplete == true, "new root publishes complete audio");
            completePath = complete!.CompletePath!;
            Check(Path.GetDirectoryName(completePath) == WebDavCachePaths.Audio(second)
                && File.ReadAllBytes(completePath).AsSpan().SequenceEqual(bytes), "audio lives under configured root/WebDav/Audio");
            cache.Configure(true, first, entry.Length);
            Check(cache.GetCompleteFiles().Count == 0, "changing cache directory clears downloaded state for the old directory");
            cache.Clear();
            var result = new byte[1024];
            Check(await complete.ReadAsync(result, 0, default) == result.Length && result.AsSpan().SequenceEqual(bytes),
                "switching roots and clearing new root preserves old active reader");
        }
        Check(File.Exists(completePath), "clearing new root does not delete old root audio on lease release");
        cache.Configure(true, second, entry.Length);
        await using (var active = cache.Acquire("same-resource", entry))
        {
            cache.Clear();
            Check(cache.GetCompleteFiles().Count == 0, "clearing cache hides downloaded state immediately even while a reader retains the file");
        }
        string cover = WebDavCachePaths.Cover(second, "fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(cover)!);
        File.WriteAllBytes(cover, bytes);
        string unrelated = Path.Combine(second, "keep.txt");
        File.WriteAllText(unrelated, "keep");
        cache.Clear();
        Check(cache.GetSize() == 0 && File.Exists(cover) && File.Exists(unrelated), "audio cleanup preserves covers and other root files");
        File.Delete(cover);
        File.Delete(unrelated);
        Directory.Delete(WebDavCachePaths.Covers(second));
        Directory.Delete(WebDavCachePaths.Audio(second));
        Directory.Delete(WebDavCachePaths.Root(second));
        Directory.Delete(second);
        Directory.Delete(WebDavCachePaths.Audio(first));
        Directory.Delete(WebDavCachePaths.Root(first));
        Directory.Delete(first);
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

internal sealed class ManualClock : TimeProvider
{
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan time) => _timestamp += time.Ticks;
}
