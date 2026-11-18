using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders;

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DOutputBuffer(D2D1BufferPrecision.Float16, D2D1ChannelDepth.Four)]
[D2DGeneratedPixelShaderDescriptor]
public readonly partial struct HdrCoverToneMap(float inputPeak, float outputPeak, float sdrWhite = 0f) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 pixel = D2D.GetInput(0);
        float4 result = pixel;
        if (sdrWhite > 0f)
        {
            float3 linear = PlaybackShaderColor.ToLinear(Hlsl.Saturate(pixel.XYZ / Hlsl.Max(pixel.W, 0.000001f)));
            float y = Hlsl.Dot(linear, new float3(0.2126f, 0.7152f, 0.0722f));
            // Most album artwork was graded for SDR. Keep diffuse colors at their
            // authored exposure and reserve a small, hue-preserving lift for near-white detail.
            float headroom = Hlsl.Max(outputPeak - sdrWhite, 0f);
            float limit = 0.25f * sdrWhite;
            float lift = limit * headroom / (limit + headroom);
            float gain = sdrWhite + lift * Hlsl.SmoothStep(0.60f, 1f, y);
            result = new float4(linear * (gain * pixel.W), pixel.W);
        }
        else
        {
            float luminance = Hlsl.Dot(pixel.XYZ, new float3(0.2126f, 0.7152f, 0.0722f));
            float knee = 0.65f * outputPeak;
            float start = knee * pixel.W;
            if (inputPeak > outputPeak && luminance > start)
            {
                // Shoulder through (knee,knee) and (inputPeak,outputPeak), with unit slope at the knee.
                float excess = (luminance - start) / Hlsl.Max(pixel.W, 0.000001f);
                float curve = 1f / (outputPeak - knee) - 1f / (inputPeak - knee);
                float mapped = knee + excess / (1f + curve * excess);
                result = new float4(pixel.XYZ * (mapped * pixel.W / luminance), pixel.W);
            }
        }
        return result;
    }
}
