using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Services.AudioAnalysis;

if (args.Length > 0 && args[0] == "--mp3-compatibility")
{
    await Mp3CompatibilityChecks.RunAsync(args.Length > 1 ? args[1] : null);
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}
static byte[] Pcm(int rate, int seconds, double frequency, bool opposite)
{
    var bytes = new byte[rate * seconds * 4];
    for (int i = 0; i < rate * seconds; i++)
    {
        short value = (short)(Math.Sin(2 * Math.PI * frequency * i / rate) * 16383);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), value);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), opposite ? (short)-value : value);
    }
    return bytes;
}
static void Wave(string path, int rate, byte[] bytes)
{
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8); writer.Write(bytes.Length + 36); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
    writer.Write((short)4); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes.Length); writer.Write(bytes);
}
static int Peak(SpectrogramData data)
{
    int offset = data.Width / 2 * data.Height;
    int bin = 0;
    for (int i = 1; i < data.Height; i++) if (data.Combined[offset + i] > data.Combined[offset + bin]) bin = i;
    return bin;
}

var accumulator = new SpectrogramAccumulator(48000, 2, 1, 2048);
var signal = new float[48000 * 2];
for (int i = 0; i < 48000; i++) { signal[i * 2] = .5f * MathF.Sin(2 * MathF.PI * 1875 * i / 48000); signal[i * 2 + 1] = -signal[i * 2]; }
for (int i = 0; i < signal.Length; i += 512) accumulator.Add(signal.AsSpan(i, Math.Min(512, signal.Length - i)));
var result = accumulator.Finish(2);
Check(Math.Abs(Peak(result) * 48000d / 2048 - 1875) < 24, "STFT preserves tone frequency");
int level = result.Combined[result.Width / 2 * result.Height + Peak(result)] - 160;
Check(Math.Abs(level + 6) <= 1, "Hann coherent gain keeps a half-scale sine near -6 dBFS");
Check(result.Combined.SequenceEqual(result.Left) && result.Left.SequenceEqual(result.Right), "opposite-phase stereo merges powers without cancellation");
var silent = new SpectrogramAccumulator(44100, 1, .001, 2048);
silent.Add(new float[44]);
Check(silent.Finish(1).Combined.All(value => value == 0), "short silent files pad safely at the level floor");

string fixture = Path.Combine(AppContext.BaseDirectory, "spectrum-fixture.wav");
Wave(fixture, 96000, Pcm(96000, 2, 30000, true));
var updates = new List<double>();
var progress = new InlineProgress(value => updates.Add(value));
using (var playbackRead = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
    result = await SpectrogramWorkerClient.AnalyzeAsync(fixture, 2048, progress, CancellationToken.None);
Check(result.SampleRate == 96000 && result.Channels == 2 && Math.Abs(Peak(result) * 96000d / 2048 - 30000) < 48,
    "published native worker preserves a 30 kHz tone at original 96 kHz rate");
Check(updates.Count > 0 && updates.SequenceEqual(updates.Order()), "worker reports monotonic bounded progress");
Check(result.Combined.SequenceEqual(result.Left), "native decoder preserves opposite-phase stereo power");
using (var exclusive = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.None))
    Check(exclusive.Length > 0, "worker releases its file handle after completion");
var database = new WinUIMusicPlayer.Services.MusicDatabaseService();
var cache = new WinUIMusicPlayer.Services.WebDav.RemoteAudioCache();
var service = new WinUIMusicPlayer.Services.SpectrogramService(database, cache);
var input = new WinUIMusicPlayer.Services.SpectrogramInput(1, 0, fixture);
var cached = await service.AnalyzeAsync(input, 2048, progress, CancellationToken.None);
Check(ReferenceEquals(cached, await service.AnalyzeAsync(input, 2048, progress, CancellationToken.None)), "completed spectra reuse the same file-version cache entry");
File.SetLastWriteTimeUtc(fixture, File.GetLastWriteTimeUtc(fixture).AddSeconds(2));
Check(!ReferenceEquals(cached, await service.AnalyzeAsync(input, 2048, progress, CancellationToken.None)), "file changes invalidate cached spectra");
database.Track = new(2, "/fixture.wav", new FileInfo(fixture).Length, "fixture-version");
Check(await service.AnalyzeAsync(new(1, 2, "webdav://2/fixture.wav"), 2048, progress, CancellationToken.None) is null,
    "uncached remote songs do not start network downloads");
cache.Configure(true, Path.Combine(AppContext.BaseDirectory, "cache"), 100 * 1024 * 1024);
string completePath = Path.Combine(WinUIMusicPlayer.Services.WebDav.WebDavCachePaths.Audio(Path.Combine(AppContext.BaseDirectory, "cache")),
    WinUIMusicPlayer.Services.WebDav.RemoteAudioCache.GetKey("webdav://2/fixture.wav", WinUIMusicPlayer.Services.WebDavLibraryService.ToEntry(database.Track)) + ".audio");
Directory.CreateDirectory(Path.GetDirectoryName(completePath)!);
File.Copy(fixture, completePath, true);
var remote = await service.AnalyzeAsync(new(1, 2, "webdav://2/fixture.wav"), 2048, progress, CancellationToken.None);
Check(remote is { SampleRate: 96000 }, "remote songs analyze only a complete cache lease");
using (var exclusive = new FileStream(completePath, FileMode.Open, FileAccess.Read, FileShare.None))
    Check(exclusive.Length > 0, "remote analysis releases the cache lease after process exit");
Wave(fixture, 48000, Pcm(48000, 2, 1875, false));
await using (var legacy = new AnalysisWorkerClient())
{
    var features = await legacy.AnalyzeAsync(fixture, CancellationToken.None);
    Check(features.Length == 384 && features.All(float.IsFinite), "the same published executable retains the legacy MIR protocol");
}
Wave(fixture, 48000, Pcm(48000, 120, 1875, false));
using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    bool cancelled = false;
    var cancelAfterDecodeStarted = new InlineProgress(value => { if (value >= 1) cancellation.Cancel(); });
    try { await SpectrogramWorkerClient.AnalyzeAsync(fixture, 8192, cancelAfterDecodeStarted, cancellation.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled, "analysis cancellation interrupts a real worker");
}
using (var exclusive = new FileStream(fixture, FileMode.Open, FileAccess.Read, FileShare.None))
    Check(exclusive.Length > 0, "cancel completes only after the worker exits and releases input");
await File.WriteAllTextAsync(fixture, "invalid audio");
bool rejected = false;
try { await SpectrogramWorkerClient.AnalyzeAsync(fixture, 2048, null, CancellationToken.None); }
catch (IOException) { rejected = true; }
Check(rejected, "malformed audio fails without hanging the protocol");
await Mp3CompatibilityChecks.RunAsync();
File.Delete(fixture);

sealed class InlineProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }
