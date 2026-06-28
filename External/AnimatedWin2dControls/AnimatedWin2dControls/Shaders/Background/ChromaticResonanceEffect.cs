using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// 移植自 <see href="https://www.shadertoy.com/view/4tf3zn"/>
    /// (原作者 Philippe Desgranges, License Creative Commons Attribution-NonCommercial-ShareAlike 3.0 Unported)。
    /// 原版使用 iChannel0 采样音频频谱做 MUSIC_REACTION 扰动；此处改用 <c>musicReaction</c> 形参（默认 0）作为占位，
    /// 便于后续接入音频总线。原版内嵌 sin 三相位彩虹已替换为调色板 4 色 lerp 扫描（color1→2→3→4 周期），
    /// 其它 5 层 Line 累加 + overlay + dither 全部保留。
    /// </summary>
    [D2DInputCount(0)]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    [D2DRequiresScenePosition]
    public readonly partial struct ChromaticResonanceEffect(
        float time,
        float2 dispatchSize,
        float3 color1,
        float3 color2,
        float3 color3,
        float3 color4,
        float musicReaction = 0f,
        bool isDark = true,
        bool enableDithering = true) : ID2D1PixelShader
    {
        private const float PI = 3.14159265359f;
        private const float PI2 = PI * 2f;
        private const float MUSIC_REACTION = 0.2f;

        // Dave Hoskins - https://www.shadertoy.com/view/4djSRW
        private static float N2(float2 p)
        {
            p = p - 1456.2346f * Hlsl.Floor(p / 1456.2346f);
            float3 p3 = Hlsl.Frac(new float3(p.X, p.Y, p.X) * new float3(443.897f, 441.423f, 437.195f));
            p3 += Hlsl.Dot(p3, new float3(p3.Y, p3.Z, p3.X) + 19.19f);
            return Hlsl.Frac((p3.X + p3.Y) * p3.Z);
        }

        private static float CosineInterpolate(float y1, float y2, float t)
        {
            float mu = (1f - Hlsl.Cos(t * PI)) * 0.5f;
            return y1 * (1f - mu) + y2 * mu;
        }

        private static float Noise2(float2 uv)
        {
            float2 corner = Hlsl.Floor(uv);
            float c00 = N2(corner + new float2(0f, 0f));
            float c01 = N2(corner + new float2(0f, 1f));
            float c11 = N2(corner + new float2(1f, 1f));
            float c10 = N2(corner + new float2(1f, 0f));

            float2 diff = Hlsl.Frac(uv);

            return CosineInterpolate(
                CosineInterpolate(c00, c10, diff.X),
                CosineInterpolate(c01, c11, diff.X),
                diff.Y);
        }

        private static float LineNoise(float x, float t)
        {
            float n = Noise2(new float2(x * 0.6f, t * 0.2f));
            return n - 0.5f;
        }

        private float Line(float2 uv, float t, float scroll)
        {
            float ax = Hlsl.Abs(uv.X);
            uv.Y *= 0.5f + ax * ax * 0.3f;

            uv.X += time * scroll;

            float n1 = LineNoise(uv.X, t);
            float n2 = LineNoise(uv.X + 0.5f, t + 10f) * 2f;

            float ay = Hlsl.Abs(uv.Y - n1);
            float lum = Hlsl.SmoothStep(0.02f, 0f, ay) * 1.5f;
            lum += Hlsl.SmoothStep(2.2f, 0f, ay) * 0.4f;

            float r = (uv.Y - n1) / (n2 - n1);
            float h = Hlsl.Saturate(1f - r);
            if (r > 0f) lum = Hlsl.Max(lum, h * h * 0.7f);

            return lum;
        }

        private static float RemapTri(float v)
        {
            float orig = v * 2f - 1f;
            v = orig / Hlsl.Sqrt(Hlsl.Abs(orig));
            v = Hlsl.Max(-1f, v);
            v = v - Hlsl.Sign(orig) + 0.5f;
            return v;
        }

        private static float3 ScreenSpaceDither(float2 vScreenPos, float time)
        {
            float colorDepth = 64f;
            float dotValue = Hlsl.Dot(new float2(131f, 312f), vScreenPos + time);
            float3 vDither = new float3(dotValue, dotValue, dotValue);
            vDither = Hlsl.Frac(vDither / new float3(103f, 71f, 97f));
            float3 remapped = new float3(RemapTri(vDither.X), RemapTri(vDither.Y), RemapTri(vDither.Z));
            return remapped / colorDepth;
        }

        public float4 Execute()
        {
            float2 fragCoord = D2D.GetScenePosition().XY;
            float2 uv = (2f * fragCoord - dispatchSize) / dispatchSize.Y;

            // 音乐反应：原版从 iChannel0 取音频频谱，此处用 uniform musicReaction 代替
            float wave  = musicReaction * MUSIC_REACTION * Hlsl.Sin(time * 0.2f);
            float wave1 = musicReaction * MUSIC_REACTION * Hlsl.Sin(time * 0.2f + 0.5f);
            float wave2 = musicReaction * MUSIC_REACTION * Hlsl.Sin(time * 0.2f + 1.0f);
            float wave3 = musicReaction * MUSIC_REACTION * Hlsl.Sin(time * 0.2f + 1.5f);
            float wave4 = musicReaction * MUSIC_REACTION * Hlsl.Sin(time * 0.2f + 2.0f);

            float lum  = Line(uv * new float2(2.0f, 1.0f)   + new float2(0.0f,  wave),  time * 0.3f,              0.1f)  * 0.6f;
            lum +=       Line(uv * new float2(1.5f, 0.9f)   + new float2(0.33f, wave1), time * 0.5f + 45.0f,       0.15f) * 0.5f;
            lum +=       Line(uv * new float2(1.3f, 1.2f)   + new float2(0.66f, wave2), time * 0.4f + 67.3f,       0.2f)  * 0.3f;
            lum +=       Line(uv * new float2(1.5f, 1.15f)  + new float2(0.8f,  wave3), time * 0.77f + 1235.45f,   0.23f) * 0.43f;
            lum +=       Line(uv * new float2(1.5f, 1.15f)  + new float2(0.8f,  wave4), time * 0.77f + 456.45f,    0.3f)  * 0.25f;

            float ax = Hlsl.Abs(uv.X);
            lum += ax * ax * 0.05f;

            // 调色板 4 色作为 hue 端点；用 sin 连续映射 t∈[0,1] 避免 Frac 跳变带（垂直扫描线）
            float p = uv.X * 0.2f + time * 0.1f;
            float t = 0.5f + 0.5f * Hlsl.Sin(p * PI2);
            float3 hue = Hlsl.Lerp(
                Hlsl.Lerp(color1, color2, t),
                Hlsl.Lerp(color3, color4, t),
                t);

            // thres 0.7→0.45 让更多像素进入 overlay 分支；dim 分支加 hue 地板避免大半背景变黑
            // 暗模式 dim 权重 0.1（背景压暗）+ 线条 brightCap 0.6（峰值不冲白）
            // 浅色模式 dim 权重 0.5 + 线条 brightCap 1.0（保持原观感）
            const float thres = 0.45f;
            float floorAmt = isDark ? 0.0f : 0.5f;
            float brightCap = isDark ? 0.45f : 1.0f;
            float3 col;
            if (lum < thres)
            {
                col = hue * (floorAmt + ((1f - floorAmt) * lum / thres));
            }
            else
            {
                float lumPast = (lum - thres) * brightCap;
                col = new float3(1f, 1f, 1f) - (new float3(1f - lumPast, 1f - lumPast, 1f - lumPast) * (new float3(1f, 1f, 1f) - hue));
            }

            float3 diter = enableDithering ? ScreenSpaceDither(fragCoord, time) : new float3(0f, 0f, 0f);

            return new float4(Hlsl.Saturate(col + diter), 1f);
        }
    }
}