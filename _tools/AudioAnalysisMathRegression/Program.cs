using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.AudioAnalysis;
var scalar = new ScalarBaseline.DspExtractor();
var simd = new DspExtractor();
var optimizedScalar = new ScalarOptimized.DspExtractor();
var input = new float[320000];
var a = new float[128]; var b = new float[128]; var reference = new float[128];
string frozen = Path.Combine(Directory.GetCurrentDirectory(), "_tools/artifacts/real-music-comparison/parity-real-frozen.f32");
if (File.Exists(frozen))
    using (var file = File.OpenRead(frozen)) file.ReadExactly(MemoryMarshal.AsBytes(input.AsSpan()));
else
    for (int i = 0; i < input.Length; i++) input[i] = .5f * MathF.Sin(2 * MathF.PI * 220 * i / 32000) + .2f * MathF.Sin(2 * MathF.PI * 440 * i / 32000);
double maximum = 0;
void Compare()
{
    scalar.Extract(input, a); simd.Extract(input, b);
    optimizedScalar.Extract(input, reference);
    if (!MemoryMarshal.AsBytes(a.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(reference.AsSpan()))) throw new Exception("Sort-only scalar feature drift");
    for (int i = 0; i < 128; i++) { if (!float.IsFinite(b[i])) throw new Exception("Nonfinite feature"); maximum = Math.Max(maximum, Math.Abs(a[i] - b[i])); }
    if (maximum > .002) throw new Exception("Feature parity drift " + maximum);
}
Compare();
for (int i = 0; i < 3; i++) { scalar.Extract(input, a); simd.Extract(input, b); }
long allocated = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < 12; i++) simd.Extract(input,b);
long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
if (bytes != 0) throw new Exception("SIMD extractor allocated " + bytes);
double Time(Action run)
{
    long start = Stopwatch.GetTimestamp();
    for (int i = 0; i < 15; i++) run();
    return Stopwatch.GetElapsedTime(start).TotalMilliseconds / 15;
}
double scalarMs = Time(() => scalar.Extract(input,a));
double optimizedMs = Time(() => optimizedScalar.Extract(input,b));
double simdMs = Time(() => simd.Extract(input,b));
Array.Clear(input); Compare();
var random = new Random(20261102);
for (int i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2 - 1);
Compare();
for (int i = 0; i < input.Length; i++) input[i] = MathF.Sin(2 * MathF.PI * 440 * i / 32000);
Compare();
foreach (int length in new[] {0,1,24,25,32,511,1025,2049})
    for (int mode = 0; mode < 5; mode++)
    {
        var data = new float[length];
        for(int i=0;i<length;i++) data[i] = mode switch {0=>random.Next(-10,10),1=>i,2=>length-i,3=>1,_=>(float)random.NextDouble()};
        var expected = (float[])data.Clone();Array.Sort(expected);
        DspExtractor.SortAscending(data);
        if(!data.SequenceEqual(expected))throw new Exception("Sort boundary "+length);
    }
Console.WriteLine($"PASS: NativeAOT={ !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported }; SIMD={Vector.IsHardwareAccelerated}; lanes={Vector<float>.Count}; parity_max={maximum:G9}; allocated={bytes}; scalar_ms={scalarMs:F3}; optimized_scalar_ms={optimizedMs:F3}; SIMD_ms={simdMs:F3}");
