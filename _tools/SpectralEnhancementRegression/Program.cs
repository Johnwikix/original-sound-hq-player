using System.Diagnostics;
using System.Text.Json;
using AudioPlayer.Decode;
using AudioPlayer.AudioEnhancement;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

ffmpeg.RootPath = AppContext.BaseDirectory;
string work = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/spectral-regression");
Directory.CreateDirectory(work);
int passed = 0;
string? goldenPath = args.FirstOrDefault(a => a.StartsWith("--golden="))?[9..];
if(goldenPath != null) NativeGoldenVerifier.Verify(Path.GetFullPath(goldenPath));
var records = new List<AudioResult>();
void Require(bool ok, string message) { if (!ok) throw new Exception(message); passed++; }
var all = SpectralEnhancementMode.LossyAndLowRateLossless;
foreach (var mode in Enum.GetValues<SpectralEnhancementMode>())
{
    byte[] payload = new byte[BinarySerializer.IpcSettingSize];
    int bytes = BinarySerializer.WriteIpcSetting(payload, new() { OutputMode = "WasapiShared", SpectralEnhancement = mode });
    Require(BinarySerializer.ReadIpcSetting(payload.AsSpan(0, bytes)).SpectralEnhancement == mode, "IPC roundtrip");
    Require(BinarySerializer.ReadIpcSetting(payload.AsSpan(0, bytes - 1)).SpectralEnhancement == SpectralEnhancementMode.Off, "legacy IPC defaults");
}
Require(new AudioPreferences().SpectralEnhancement == SpectralEnhancementMode.Off, "default disabled");
Require((new AudioPreferences { SpectralEnhancement = (SpectralEnhancementMode)255 }).Sanitize().SpectralEnhancement == SpectralEnhancementMode.Off, "invalid preference normalized");
foreach (bool dsd in new[] { false, true })
foreach (bool spatial in new[] { false, true })
foreach (int channels in new[] { 1, 2, 6 })
foreach (int rate in new[] { 44100, 48000, 96000, 192000 })
foreach (bool lossless in new[] { false, true })
foreach (var mode in Enum.GetValues<SpectralEnhancementMode>())
    Require(SpectralEnhancementPolicy.IsEligible(mode, rate, channels, lossless, dsd, spatial)
        == (mode != SpectralEnhancementMode.Off && !dsd && !spatial && channels <= 2 && rate <= 48000
            && (!lossless || mode == all)), "eligibility matrix");
foreach (var (device, target) in new[] { (48000,48000), (96000,96000), (192000,192000), (384000,192000), (352800,176400), (768000,192000) })
    Require(SpectralEnhancementPolicy.OutputRate(44100, device) == target, "shared divisor");
Require(SpectralEnhancementPolicy.OutputRate(44100, null) == 88200, "exclusive 44.1k 2x");
Require(SpectralEnhancementPolicy.OutputRate(48000, null) == 96000, "ASIO 48k 2x");

foreach (int rate in new[] { 44100,48000 })
foreach (int channels in new[] { 1,2 })
foreach (int frames in new[] { 1,127,256,8193 })
{
    string path = Path.Combine(work, $"{rate}-{channels}-{frames}.wav");
    WriteWav(path, rate, channels, frames);
    using var dec = new PcmDecoder();
    Require(dec.Open(path,88200,6,enhancement:all), "open PCM");
    Require(dec.SampleRate == rate * 2, "exclusive actual session rate");
    var buffer = new double[317 * channels];
    var actual = new List<double>();
    int read;
    while ((read=dec.Read(buffer)) > 0) actual.AddRange(buffer.AsSpan(0,read*channels).ToArray());
    Require(actual.Count == frames * 2 * channels, $"trim duration {rate}/{frames}: {actual.Count}");
    double[] expected = Reference(rate, channels, frames);
    Require(actual.SequenceEqual(expected), $"block bridge exact {rate}/{channels}/{frames}");
    Require(dec.SeekToMs(0), "seek resets");
    int repeat = dec.Read(buffer);
    Require(buffer.AsSpan(0,repeat*channels).SequenceEqual(actual.ToArray().AsSpan(0,repeat*channels)), "seek deterministic signal state");
}

foreach(int lowRate in new[] {8000,16000,22050,32000})
{
    string lowFile=Path.Combine(work,$"low-{lowRate}.wav");
    WriteWav(lowFile,lowRate,1,8193);
    using var low=new PcmDecoder();
    Require(low.Open(lowFile,88200,6,enhancement:all),"low rate opens");
    Require(low.SampleRate==lowRate*2,"low rate exclusive doubles source rate");
    long total=0;
    double[] lowBuffer=new double[517];
    int lowRead;
    while((lowRead=low.Read(lowBuffer))>0) total+=lowRead;
    Require(Math.Abs(total-8193*2)<=2,$"low rate duration {lowRate}: {total}");
}

string allocationTone = Path.Combine(work,"allocation.wav");
WriteWav(allocationTone,48000,2,48000*5);
using (var dec = new PcmDecoder())
{
    Require(dec.Open(allocationTone,88200,6,enhancement:all),"allocation stream opens");
    double[] block = new double[2048];
    for(int i=0;i<64;i++) dec.Read(block);
    long before=GC.GetAllocatedBytesForCurrentThread();
    while(dec.Read(block)>0) { }
    long delta=GC.GetAllocatedBytesForCurrentThread()-before;
    Require(delta==0,$"steady decoder allocated {delta} bytes");
    Console.WriteLine($"Steady decode+enhancement managed allocations: {delta} bytes");
}

string tone = Path.Combine(work,"44100-2-8193.wav");
foreach (int device in new[] { 48000,96000,192000,384000,352800 })
{
    using var dec = new PcmDecoder();
    Require(dec.Open(tone,88200,6,enhancement:all,enhancementDeviceRate:device), "open shared");
    int target = SpectralEnhancementPolicy.OutputRate(44100,device);
    Require(dec.SampleRate==target,"negotiated shared rate");
    double[] block = new double[514];
    long count=0;
    int got;
    while ((got=dec.Read(block))>0) count+=got;
    Require(Math.Abs(count-8193.0*target/44100)<=1,"resampling duration");
}
using (var dec = new PcmDecoder())
{
    Require(dec.Open(tone,88200,6,enhancement:SpectralEnhancementMode.LossyOnly),"lossy only opens lossless");
    Require(dec.SampleRate==44100,"lossy only bypasses lossless");
}
string high = Path.Combine(work,"192k.wav");
WriteWav(high,192000,2,1024);
using (var dec = new PcmDecoder())
{
    Require(dec.Open(high,88200,6,enhancement:all),"high rate opens");
    Require(dec.SampleRate==192000,"high rate lossless bypass");
}
string spatialFile=Path.GetFullPath("_tools/PlaybackSwitchRegression/Fixtures/eac3-5.1.m4a");
using (var dec = new PcmDecoder())
{
    Require(dec.Open(spatialFile,88200,6,maxChannels:2,enhancement:all),"spatial PCM fallback opens");
    Require(dec.SampleRate==48000,"spatial fallback bypass");
}

if (args.Contains("--real"))
foreach (string file in Directory.GetFiles(@"D:\audioTest"))
{
    using var plain = new PcmDecoder();
    Require(plain.Open(file,88200,6,maxChannels:2),"real baseline open");
    double[] block = new double[8192 * plain.Channels];
    long baseFrames=0;
    int got;
    while((got=plain.Read(block))>0) baseFrames+=got;
    using var dec = new PcmDecoder();
    Require(dec.Open(file,88200,6,maxChannels:2,enhancement:all),"real enhanced open");
    // Warm tiered compilation before timing the steady decode+enhancement path.
    while(dec.Read(block)>0) { if (dec.SeekToMs(0)) break; }
    long samples=0,allocated=GC.GetAllocatedBytesForCurrentThread();
    long worst=0;
    var watch=Stopwatch.StartNew();
    while(true)
    {
        long start=Stopwatch.GetTimestamp();
        got=dec.Read(block);
        worst=Math.Max(worst,Stopwatch.GetTimestamp()-start);
        if(got==0) break;
        samples+=got;
    }
    watch.Stop();
    allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
    Require(Math.Abs(samples-(double)baseFrames*dec.SampleRate/plain.SampleRate)<=1,"real duration preserved");
    var record=new AudioResult(Path.GetFileName(file),plain.SampleRate,dec.SampleRate,
        samples,samples/(double)dec.SampleRate,watch.Elapsed.TotalSeconds,
        samples/(double)dec.SampleRate/watch.Elapsed.TotalSeconds,allocated,
        worst*1000.0/Stopwatch.Frequency);
    records.Add(record);
    Console.WriteLine(JsonSerializer.Serialize(record,RegressionJsonContext.Default.AudioResult));
}
File.WriteAllText(Path.Combine(work,"results.json"),JsonSerializer.Serialize(new RegressionResult(passed,records,System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported,System.Runtime.Intrinsics.X86.Avx.IsSupported,System.Runtime.Intrinsics.X86.Sse2.IsSupported),RegressionJsonContext.Default.RegressionResult));
Console.WriteLine($"PASS {passed} assertions");

static short Sample(int frame,int channel) => (short)(Math.Sin(frame*0.1+channel*0.3)*14000);
static void WriteWav(string path,int rate,int channels,int frames)
{
    using var file = new BinaryWriter(File.Create(path));
    file.Write("RIFF"u8); file.Write(36+frames*channels*2); file.Write("WAVEfmt "u8); file.Write(16);
    file.Write((short)1);file.Write((short)channels);file.Write(rate);file.Write(rate*channels*2);
    file.Write((short)(channels*2));file.Write((short)16);file.Write("data"u8);file.Write(frames*channels*2);
    for(int i=0;i<frames;i++)for(int ch=0;ch<channels;ch++)file.Write(Sample(i,ch));
}
static double[] Reference(int rate,int channels,int frames)
{
    var core = new SpectralEnhancer(new() {InputSampleRate=rate,OutputSampleRate=rate*2,SourceFormatSelector=16,AverageBitRateKbps=192});
    var ap = new AllPassProcessor(rate,0,192000,16);
    int skip=core.LatencyFrames+(rate==44100?5:6)*2;
    int[] input=new int[core.InputFramesPerBlock*2],middle=new int[input.Length],output=new int[1024];
    var result=new List<double>();
    int source=0;
    while(result.Count<frames*2*channels)
    {
        for(int i=0;i<core.InputFramesPerBlock;i++)
        {
            input[i*2]=source+i<frames ? Sample(source+i,0)<<16 : 0;
            input[i*2+1]=source+i<frames ? Sample(source+i,channels==1?0:1)<<16 : 0;
        }
        source+=core.InputFramesPerBlock;
        ap.ProcessBlock(input,middle);core.ProcessBlock(middle,output);
        int offset=Math.Min(skip,512);skip-=offset;
        for(int i=offset;i<512&&result.Count<frames*2*channels;i++)
            for(int ch=0;ch<channels;ch++)result.Add(output[i*2+ch]/2147483648.0);
    }
    return result.ToArray();
}

internal sealed record AudioResult(string file,int sourceRate,int outputRate,long frames,double seconds,
    double processingSeconds,double realtimeFactor,long allocatedBytes,double worstReadMs);
internal sealed record RegressionResult(int passed,List<AudioResult> records,bool dynamicCodeSupported,bool avx,bool sse2);
[System.Text.Json.Serialization.JsonSerializable(typeof(AudioResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RegressionResult))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented=true)]
internal partial class RegressionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
