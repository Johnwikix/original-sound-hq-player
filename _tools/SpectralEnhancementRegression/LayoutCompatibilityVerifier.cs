using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioPlayer.AudioEnhancement;

/// <summary>使用重构前捕获的 PCM 摘要验证状态和资源布局兼容性。</summary>
internal static class LayoutCompatibilityVerifier
{
    internal static void Verify()
    {
        using var capture = new MemoryStream();
        using var writer = new BinaryWriter(capture);
        uint random = 1234567;
        int Next(int count) { random = unchecked(random * 1664525 + 1013904223); return (int)((random >> 8) % (uint)count); }
        int[] sourceRates = [0,8000,11025,12000,16000,22050,24000,32000,44100,48000,64000,88200,96000,128000,144000,176400,192000];
        int[] offsets = [-601,-600,-120,0,3,60];
        int[] bitrates = [0,4,64,128,192,320,1020];
        long samples = 0;
        for (int test = 0; test < 1536; test++)
        {
            int rate = Next(2) == 0 ? 44100 : 48000;
            int mode = Next(4);
            int ratio = mode == 1 ? 1 : new[] {1,2,4}[Next(3)];
            var options = new SpectralEnhancementOptions
            {
                InputSampleRate=rate,OutputSampleRate=rate*ratio,Mode=mode,
                OriginalSampleRate=sourceRates[Next(sourceRates.Length)],SourceFormatSelector=Next(18),
                VariableBitRate=Next(2)==0,BandBoundaryOverride=Next(10) is int boundary && boundary!=0 ? boundary+7 : 0,
                AverageBitRateKbps=bitrates[Next(bitrates.Length)],HeadMarginTenthsDb=new[] {0,1,12,60,2000}[Next(5)],
                DcrEnabled=Next(2)==0,GainCompensationTenths=Next(151),EnhancementOffsetTenthsDb=offsets[Next(offsets.Length)]
            };
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            {
                var core = new SpectralEnhancer(options);
                var allPass = new AllPassProcessor(rate,Next(11),192000,new[] {8,16,20,24,32}[Next(5)]);
                writer.Write(core.InputFramesPerBlock);writer.Write(core.LatencyFrames);
                writer.Write(core.BandBoundaryIndex);writer.Write(core.BandRepairActive);writer.Write(core.RateExtensionActive);
                int[] input=new int[core.InputFramesPerBlock*2],middle=new int[input.Length],output=new int[1024];
                for(int block=0;block<12;block++)
                {
                    for(int i=0;i<input.Length;i++) input[i]=block==0 ? 0 : block==1 ? (i==0?int.MinValue:int.MaxValue) : unchecked((int)(random=unchecked(random*1664525+1013904223)));
                    allPass.ProcessBlock(input,middle);core.ProcessBlock(middle,output);
                    digest.AppendData(MemoryMarshal.AsBytes(output.AsSpan()));samples+=output.Length;
                }
                writer.Write(digest.GetHashAndReset());
            }
        }

        writer.Flush();
        string actual = Convert.ToHexString(SHA256.HashData(capture.GetBuffer().AsSpan(0,checked((int)capture.Length))));
        const string expected = "EA116BBB1BADB419C9C262FB9F51471542F5B4B9A4D9769F46B346394304BB28";
        if (!actual.Equals(expected,StringComparison.Ordinal))
            throw new InvalidDataException($"DSP layout output changed: {actual}");
        Console.WriteLine($"PASS layout compatibility: 1536 configurations, {samples} PCM samples");
    }
}
