using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders;

[D2DInputCount(2)]
[D2DInputSimple(0)]
[D2DInputComplex(1)]
[D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
[D2DRequiresScenePosition]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
[D2DOutputBuffer(D2D1BufferPrecision.Float16, D2D1ChannelDepth.Four)]
internal readonly partial struct PlaybackLyricsBackdropEffect(float referenceWhite, float outlineRadius) : ID2D1PixelShader
{
    public float4 Execute()
    {
        // The backdrop is over the dark scene clear. Its premultiplied RGB already
        // represents that source-over result even if the shader missed a frame.
        float3 background = Hlsl.Max(D2D.GetInput(0).XYZ, 0f);
        float4 mask = D2D.GetInput(1);
        float coverage = Hlsl.Saturate(mask.B);
        float silhouette = Hlsl.Saturate(mask.R + mask.B);
        float brightest = Hlsl.Max(background.R, Hlsl.Max(background.G, background.B));
        float luminance = Hlsl.Dot(background, new float3(0.2126f, 0.7152f, 0.0722f)) / referenceWhite;
        float outlineStrength = Hlsl.SmoothStep(0.22f, 0.55f, luminance);

        // The dark contour gives the ink an edge when the backdrop has little
        // room for an additive lift. It is outside the existing glyph coverage.
        float outline = 0f;
        if (outlineStrength > 0f)
        {
            float2 position = D2D.GetScenePosition().XY;
            float2 horizontal = new(outlineRadius, 0f);
            float2 vertical = new(0f, outlineRadius);
            float neighbor = Hlsl.Max(
                Hlsl.Max(SampleSilhouette(position + horizontal), SampleSilhouette(position - horizontal)),
                Hlsl.Max(SampleSilhouette(position + vertical), SampleSilhouette(position - vertical)));
            outline = Hlsl.Saturate(2.2f * (neighbor - silhouette)) * outlineStrength;
        }

        float3 ink = background * (referenceWhite / Hlsl.Max(referenceWhite, brightest));
        float lift = Hlsl.Min(0.42f * referenceWhite, 0.82f * Hlsl.Max(0f, referenceWhite - brightest));
        ink += lift;

        float remainingOutline = outline * (1f - silhouette);
        return new float4(ink * coverage + 0.035f * referenceWhite * remainingOutline,
            coverage + remainingOutline);
    }

    private static float SampleSilhouette(float2 position)
    {
        float4 sample = D2D.SampleInputAtPosition(1, position);
        return Hlsl.Saturate(sample.R + sample.B);
    }
}
