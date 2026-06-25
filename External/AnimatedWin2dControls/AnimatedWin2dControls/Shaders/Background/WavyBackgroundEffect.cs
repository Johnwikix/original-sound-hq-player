using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// 移植自 <see href="https://github.com/ghost1372/DevWinUI/blob/main/dev/DevWinUI.Shader/Shaders/WavyBackgroundShader.cs"/>
    /// (原作出处 <see href="https://www.shadertoy.com/view/ltGSWD"/>)。
    /// 原版硬编码 2 色 (深蓝 → 浅蓝) 背景 + 1 个 wave 加成。背景重写为 color1/color2/color4 三色双线性渐变，
    /// 并叠加 PS3 风格"呼吸"复合 warp（宽高比校正后的 ±60° 双向 sin 旋转，sin(time*0.3)，周期≈21s；
    /// 再叠加低速 sin 漂移，速度 time*1.5，振幅 1/30 与 1/60）。color3 仍只用于 wave 叠色。
    /// </summary>
    [D2DInputCount(0)]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    [D2DRequiresScenePosition]
    public readonly partial struct WavyBackgroundEffect(
        float time,
        float2 dispatchSize,
        float3 color1,
        float3 color2,
        float3 color3,
        float3 color4,
        bool enableDithering = true) : ID2D1PixelShader
    {
        private static float Gradient(float p)
        {
            float2 pt0 = new float2(0.00f, 0.0f);
            float2 pt1 = new float2(0.86f, 0.1f);
            float2 pt2 = new float2(0.955f, 0.40f);
            float2 pt3 = new float2(0.99f, 1.0f);
            float2 pt4 = new float2(1.00f, 0.0f);

            if (p < pt0.X) return pt0.Y;

            if (p < pt1.X)
                return Hlsl.Lerp(pt0.Y, pt1.Y, (p - pt0.X) / (pt1.X - pt0.X));

            if (p < pt2.X)
                return Hlsl.Lerp(pt1.Y, pt2.Y, (p - pt1.X) / (pt2.X - pt1.X));

            if (p < pt3.X)
                return Hlsl.Lerp(pt2.Y, pt3.Y, (p - pt2.X) / (pt3.X - pt2.X));

            if (p < pt4.X)
                return Hlsl.Lerp(pt3.Y, pt4.Y, (p - pt3.X) / (pt4.X - pt3.X));

            return pt4.Y;
        }

        private float WaveN(
            float2 uv,
            float2 s12,
            float2 t12,
            float2 f12,
            float2 h12)
        {
            float2 x12 =
                Hlsl.Sin((time * s12 + t12 + uv.X) * f12) * h12;

            float g = Gradient(
                uv.Y / (0.5f + x12.X + x12.Y));

            return g * 0.27f;
        }

        private float Wave1(float2 uv)
        {
            return WaveN(
                new float2(uv.X, uv.Y - 0.25f),
                new float2(0.03f, 0.06f),
                new float2(0.00f, 0.02f),
                new float2(8.0f, 3.7f),
                new float2(0.06f, 0.05f));
        }

        private float Wave2(float2 uv)
        {
            return WaveN(
                new float2(uv.X, uv.Y - 0.25f),
                new float2(0.04f, 0.07f),
                new float2(0.16f, -0.37f),
                new float2(6.7f, 2.89f),
                new float2(0.06f, 0.05f));
        }

        private float Wave3(float2 uv)
        {
            return WaveN(
                new float2(uv.X, 0.75f - uv.Y),
                new float2(0.035f, 0.055f),
                new float2(-0.09f, 0.27f),
                new float2(7.4f, 2.51f),
                new float2(0.06f, 0.05f));
        }

        private float Wave4(float2 uv)
        {
            return WaveN(
                new float2(uv.X, 0.75f - uv.Y),
                new float2(0.032f, 0.09f),
                new float2(0.08f, -0.22f),
                new float2(6.5f, 3.89f),
                new float2(0.06f, 0.05f));
        }

        private static float RemapTri(float v)
        {
            float orig = v * 2.0f - 1.0f;
            v = orig / Hlsl.Sqrt(Hlsl.Abs(orig));
            v = Hlsl.Max(-1.0f, v);
            v = v - Hlsl.Sign(orig) + 0.5f;
            return v;
        }

        private static float3 ScreenSpaceDither(float2 vScreenPos, float time)
        {
            float colorDepth = 64.0f;
            float dotValue = Hlsl.Dot(new float2(131.0f, 312.0f), vScreenPos.XY + time);
            float3 vDither = new float3(dotValue, dotValue, dotValue);
            vDither.XYZ = Hlsl.Frac(vDither.XYZ / new float3(103.0f, 71.0f, 97.0f));
            float3 remapped = new float3(RemapTri(vDither.X), RemapTri(vDither.Y), RemapTri(vDither.Z));
            return remapped / colorDepth;
        }

        public float4 Execute()
        {
            float2 uv = D2D.GetScenePosition().XY / dispatchSize;

            float2 scene = D2D.GetScenePosition().XY;

            float waves =
                Wave1(uv) +
                Wave2(uv) +
                Wave3(uv) +
                Wave4(uv);

            // 背景：color1/color2/color4 双线性 + PS3 风格"呼吸"复合 warp（强烈档）
            float ratio = dispatchSize.X / dispatchSize.Y;
            float2 tuv = uv - 0.5f;
            tuv.Y *= 1.0f / ratio;
            float angle = Hlsl.Sin(time * 0.3f) * 1.0472f;  // 1.0472 rad = π/3 = 60°
            float cR = Hlsl.Cos(angle);
            float sR = Hlsl.Sin(angle);
            tuv = new float2(cR * tuv.X - sR * tuv.Y, sR * tuv.X + cR * tuv.Y);
            tuv.Y *= ratio;
            float slowSpeed = time * 1.5f;
            tuv.X += Hlsl.Sin(tuv.Y * 5.0f + slowSpeed) / 30.0f;
            tuv.Y += Hlsl.Sin(tuv.X * 7.5f + slowSpeed) / 60.0f;
            tuv += 0.5f;

            float3 bg = Hlsl.Lerp(
                Hlsl.Lerp(color1, color2, tuv.X),
                Hlsl.Lerp(color4, color1, tuv.X),
                tuv.Y);

            // wave 叠色：用 color3 给波纹染色 (替代原硬编码白波纹)
            float3 color = bg + waves * color3;

            float3 diter = enableDithering ? ScreenSpaceDither(scene, time) : new float3(0.0f, 0.0f, 0.0f);

            return new float4(Hlsl.Saturate(color + diter), 1f);
        }
    }
}
