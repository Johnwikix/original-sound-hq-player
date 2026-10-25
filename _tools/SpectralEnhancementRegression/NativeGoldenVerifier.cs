using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AudioPlayer.AudioEnhancement;

/// <summary>Compares the current compiled DSP with captured original-filter PCM and all-pass state.</summary>
internal static class NativeGoldenVerifier
{
    internal static (int Cases,long Samples) Verify(string folder)
    {
        int cases=0;
        long samples=0;
        foreach(string path in Directory.GetDirectories(folder,"case-*"))
        {
            byte[] config=File.ReadAllBytes(Path.Combine(path,"config.bin"));
            int[] fields=new int[12];
            for(int i=0;i<12;i++) fields[i]=BinaryPrimitives.ReadInt32LittleEndian(config.AsSpan(i*4,4));
            int bits=int.Parse(File.ReadAllText(Path.Combine(path,"source-bits.txt")));
            int selector=fields[3];
            int sourceMode=selector switch {0 or 1=>4,3=>2,5=>9,16=>0,101=>1,_=>3};
            var allPass=new AllPassProcessor(fields[1],sourceMode,fields[6]==0?128000:fields[6]*1000,bits);
            var core=new SpectralEnhancer(new() { InputSampleRate=fields[1],OutputSampleRate=fields[2],
                SourceFormatSelector=selector,Mode=fields[0],OriginalSampleRate=fields[4],AverageBitRateKbps=fields[6],
                HeadMarginTenthsDb=fields[8],GainCompensationTenths=fields[10],EnhancementOffsetTenthsDb=fields[11] });
            byte[] input=File.ReadAllBytes(Path.Combine(path,"input.s32le"));
            byte[] expected=File.ReadAllBytes(Path.Combine(path,"output.s32le"));
            byte[] expectedMiddle=File.ReadAllBytes(Path.Combine(path,"heq-output.s32le"));
            int[] middle=new int[input.Length/4],output=new int[expected.Length/4];
            ReadOnlySpan<int> integerInput=MemoryMarshal.Cast<byte,int>(input);
            int size=core.InputFramesPerBlock*2;
            for(int block=0;block<64;block++)
            {
                allPass.ProcessBlock(integerInput.Slice(block*size,size),middle.AsSpan(block*size,size));
                core.ProcessBlock(middle.AsSpan(block*size,size),output.AsSpan(block*1024,1024));
            }
            if(!MemoryMarshal.AsBytes(output.AsSpan()).SequenceEqual(expected)
                ||!MemoryMarshal.AsBytes(middle.AsSpan()).SequenceEqual(expectedMiddle)
                ||!allPass.SnapshotState().AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(path,"heq-final.bin"))))
                throw new Exception($"Original native filter mismatch: {Path.GetFileName(path)}");
            cases++;
            samples+=output.Length;
        }
        if(cases!=48) throw new Exception($"Expected 48 original-filter captures, got {cases}");
        Console.WriteLine($"PASS native golden: {cases} cases, {samples} scalar samples, 0 LSB difference; all-pass PCM and state exact");
        return(cases,samples);
    }
}
