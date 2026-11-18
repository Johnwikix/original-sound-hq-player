using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders;

internal static class PlaybackShaderColor
{
    public static float3 ToLinear(float3 color)
    {
        return new float3(Decode(color.X), Decode(color.Y), Decode(color.Z));
    }

    private static float Decode(float component)
    {
        float channel = Hlsl.Max(component, 0f);
        return channel <= 0.04045f ? channel / 12.92f : Hlsl.Pow((channel + 0.055f) / 1.055f, 2.4f);
    }

    public static float3 Background(float3 srgb, float white, float peak, float crest)
    {
        float3 linear = ToLinear(srgb);
        float signal = Hlsl.Max(linear.X, Hlsl.Max(linear.Y, linear.Z));
        float headroom = Hlsl.Max(peak - white, 0f) / Hlsl.Max(peak, 0.0001f);
        float feature = Hlsl.Saturate(crest);

        // Keep the color's own luminance in the highlight signal. Amplifying a
        // normalized hue alone can turn a nearly black contour into a peak-white stripe.
        float exposed = signal * (1f + 0.39f * headroom * feature * feature);
        const float knee = 1f;
        float range = 0.40f * headroom;
        float excess = Hlsl.Max(0f, exposed - knee);
        // The shoulder keeps the same slope on both sides of the knee and
        // asymptotically limits the background to a fraction of the available headroom.
        float rolled = Hlsl.Min(exposed, knee) + excess * range / Hlsl.Max(0.00001f, range + excess);
        return linear * (white * rolled / Hlsl.Max(0.00001f, signal));
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
[D2DOutputBuffer(D2D1BufferPrecision.Float16, D2D1ChannelDepth.Four)]
internal readonly partial struct ScRgbPlaybackEffect(float white) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 pixel = D2D.GetInput(0);
        if (pixel.W <= 0.00001f) return float4.Zero;
        return new float4(PlaybackShaderColor.ToLinear(pixel.XYZ / pixel.W) * (white * pixel.W), pixel.W);
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
[D2DOutputBuffer(D2D1BufferPrecision.Float16, D2D1ChannelDepth.Four)]
internal readonly partial struct PlaybackLyricSweepEffect(
    float4 played, float4 unplayed, float range) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 input = D2D.GetInput(0);
        float progress = Hlsl.Saturate((1f - input.X / Hlsl.Max(input.W, 0.00001f)) * range);
        float4 ink = Hlsl.Lerp(played, unplayed, Hlsl.SmoothStep(0f, 1f, progress));
        float alpha = ink.W * input.W;
        return new float4(ink.XYZ * alpha, alpha);
    }
}

internal static class PlaybackTextGlow
{
    public static float LightInkWeight(float4 pixel, float white)
    {
        float luminance = Hlsl.Dot(pixel.XYZ, new float3(0.2126f, 0.7152f, 0.0722f));
        return Hlsl.SmoothStep(0.15f, 0.35f, luminance / Hlsl.Max(pixel.W * white, 0.00001f));
    }
}

[D2DInputCount(1)]
[D2DInputSimple(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
[D2DOutputBuffer(D2D1BufferPrecision.Float16, D2D1ChannelDepth.Four)]
internal readonly partial struct PlaybackTextGlowCoreEffect(float white, float peak, float strength) : ID2D1PixelShader
{
    public float4 Execute()
    {
        float4 ink = D2D.GetInput(0);
        float gain = 1f + (peak / white - 1f) * strength * PlaybackTextGlow.LightInkWeight(ink, white);
        return new float4(ink.XYZ * gain, ink.W);
    }
}
