using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background;

/// <summary>
/// Folia 壁纸背景的本地 GPU 管线。
///
/// 所有壁纸歌词模式共享 Folia 的通用几何背景；模式差异由前景排版和
/// 装饰图层表达。文字仍由 Win2D 负责字形栅格化，避免字体回退和 CJK 字形由
/// shader 重建造成的差异。mode 参数保留给未来背景模式注册，不在当前版本接入频谱。
/// </summary>
[D2DInputCount(0)]
[D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
[D2DGeneratedPixelShaderDescriptor]
[D2DRequiresScenePosition]
public readonly partial struct FoliaWallpaperEffect(
    float time,
    float2 dispatchSize,
    float3 backgroundColor,
    float3 secondaryColor,
    float3 primaryColor,
    float3 accentColor,
    float mode,
    float intensity = 1f) : ID2D1PixelShader
{
    private static float Hash(float2 value)
        => Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(value, new float2(127.1f, 311.7f))) * 43758.5453f);

    private static float2 Rotate(float2 value, float angle)
    {
        float sine = Hlsl.Sin(angle);
        float cosine = Hlsl.Cos(angle);
        return new float2(value.X * cosine - value.Y * sine, value.X * sine + value.Y * cosine);
    }

    private static float ShapeMask(float2 point, float2 center, float radius, float angle, float shape, float filled, float pixel)
    {
        float2 local = Rotate(point - center, angle);
        float distance;

        if (shape < 1f)
        {
            distance = Hlsl.Length(local) - radius;
        }
        else if (shape < 2f)
        {
            distance = Hlsl.Max(Hlsl.Abs(local.X), Hlsl.Abs(local.Y)) - radius;
        }
        else if (shape < 3f)
        {
            // A compact equilateral-like triangle, matching the CSS polygon used upstream.
            distance = Hlsl.Max(local.Y * 0.86f + Hlsl.Abs(local.X) * 0.5f, -local.Y * 0.86f) - radius * 0.86f;
        }
        else
        {
            float horizontal = Hlsl.Max(Hlsl.Abs(local.X) - radius * 0.28f, Hlsl.Abs(local.Y) - radius);
            float vertical = Hlsl.Max(Hlsl.Abs(local.X) - radius, Hlsl.Abs(local.Y) - radius * 0.28f);
            distance = Hlsl.Min(horizontal, vertical);
        }

        float fillMask = 1f - Hlsl.SmoothStep(0f, pixel * 1.5f, distance);
        float strokeMask = 1f - Hlsl.SmoothStep(0f, pixel * 1.25f, Hlsl.Abs(distance));
        return Hlsl.Lerp(strokeMask, fillMask, filled);
    }

    private float3 RenderCommonBackground(float2 point, float2 uv, float aspect)
    {
        // Folia's common background is a theme-colored field with a deterministic
        // geometric layer. The web implementation creates 15 shapes and 20
        // particles; the same counts and low-opacity treatment are reproduced here
        // so the native wallpaper remains sparse instead of becoming a full-screen
        // texture. `intensity` is reserved for the future audio-band input.
        float phase = time * (0.045f + intensity * 0.005f);
        float3 color = backgroundColor;

        for (int i = 0; i < 15; i++)
        {
            float index = i + 1f;
            float seed = Hash(new float2(index * 13.17f, index * 4.71f));
            float seedY = Hash(new float2(index * 7.31f, index * 19.23f));
            float shape = Hlsl.Floor(Hash(new float2(index * 3.19f, index * 11.07f)) * 4f);
            float filled = Hlsl.Step(0.7f, Hash(new float2(index * 23.1f, index * 5.7f)));
            float radius = (20f + seed * 50f) / Hlsl.Max(dispatchSize.Y, 1f);
            float2 center = new float2(
                (seed - 0.5f) * aspect + Hlsl.Sin(phase + index) * 15f / Hlsl.Max(dispatchSize.Y, 1f),
                (seedY - 0.5f) + Hlsl.Cos(phase * 0.9f + index * 1.37f) * 30f / Hlsl.Max(dispatchSize.Y, 1f));
            float angle = Hash(new float2(index * 8.1f, index * 2.4f)) * 6.2831853f + phase * 0.12f;
            float opacity = (0.11f + seedY * 0.08f) * Hlsl.Saturate(intensity);
            float mask = ShapeMask(point, center, radius, angle, shape, filled,
                1f / Hlsl.Max(dispatchSize.Y, 1f));
            color = Hlsl.Lerp(color, secondaryColor, mask * opacity);
        }

        for (int j = 0; j < 20; j++)
        {
            float index = j + 1f;
            float x = Hash(new float2(index * 31.7f, index * 9.1f));
            float y = Hash(new float2(index * 17.3f, index * 27.9f));
            float2 center = new float2(
                (x - 0.5f) * aspect,
                (y - 0.5f) + Hlsl.Sin(phase * 0.8f + index) * 100f / Hlsl.Max(dispatchSize.Y, 1f));
            float radius = (1f + Hash(new float2(index * 4.2f, index * 14.8f)) * 3f) / Hlsl.Max(dispatchSize.Y, 1f);
            float mask = 1f - Hlsl.SmoothStep(0f, radius, Hlsl.Length(point - center));
            color = Hlsl.Lerp(color, accentColor, mask * (0.08f + x * 0.22f) * Hlsl.Saturate(intensity));
        }

        float reservedPrimary = primaryColor.X * 0f;
        return Hlsl.Saturate(color + reservedPrimary);
    }

    private float3 RenderMode(float2 point, float2 uv, float aspect)
        => RenderCommonBackground(point, uv, aspect);

    public float4 Execute()
    {
        // Keep the mode slot in the constant buffer even while the common
        // background is selected for every composition.
        float reservedMode = mode * 0f;
        float2 pixel = D2D.GetScenePosition().XY;
        float2 uv = pixel / Hlsl.Max(dispatchSize, new float2(1f, 1f));
        float aspect = dispatchSize.X / Hlsl.Max(dispatchSize.Y, 1f);
        float2 point = (uv - 0.5f) * new float2(aspect, 1f);

        float3 color = RenderMode(point, uv, aspect) + reservedMode;
        float vignette = Hlsl.SmoothStep(0.4f, 1.05f, Hlsl.Length(point));
        color *= 1f - vignette * 0.6f;
        return new float4(Hlsl.Saturate(color), 1f);
    }
}
