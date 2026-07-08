using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{

    // 来自 https://www.shadertoy.com/view/sfsSDs
    [D2DInputCount(0)]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    [D2DRequiresScenePosition]
    public readonly partial struct LiquidFlowEffect(
        float time,
        float2 dispatchSize,
        float3 color1,
        float3 color2,
        float3 color3,
        float3 color4,
        bool enableDithering = true,
        bool isDark = true) : ID2D1PixelShader
    {
        private const int AA = 2;

        private static float2 R(float2 v, float t)
        {
            float s = Hlsl.Sin(t);
            float c = Hlsl.Cos(t);
            return new float2(c * v.X - s * v.Y, s * v.X + c * v.Y);
        }

        private static float3 Aces(float3 c)
        {
            float3x3 m1 = new float3x3(
                0.59719f, 0.35458f, 0.04823f,
                0.07600f, 0.90834f, 0.01566f,
                0.02840f, 0.13383f, 0.83777f);
            float3x3 m2 = new float3x3(
                1.60475f, -0.53108f, -0.07367f,
                -0.10208f, 1.10813f, -0.00605f,
                -0.00327f, -0.07276f, 1.07602f);

            float3 v = Hlsl.Mul(m1, c);
            float3 aVal = v * (v + 0.0245786f) - 0.000090537f;
            float3 bVal = v * (0.983729f * v + 0.4329510f) + 0.238081f;
            return Hlsl.Mul(m2, aVal / bVal);
        }

        private static float Noise(float3 p)
        {
            const float PHI = 1.618033988f;
            float3x3 gold = new float3x3(
                -0.571464913f, -0.278044873f, +0.772087367f,
                +0.814921382f, -0.303026659f, +0.494042493f,
                +0.096597072f, +0.911518454f, +0.399753815f);

            return Hlsl.Dot(
                Hlsl.Cos(Hlsl.Mul(gold, p)),
                Hlsl.Sin(Hlsl.Mul(PHI * p, gold)));
        }

        private static float Dither(float2 pos)
        {
            return Hlsl.Frac(52.9829189f * Hlsl.Frac(Hlsl.Dot(pos, new float2(0.06711056f, 0.00583715f))));
        }

        public float4 Execute()
        {
            float2 U = D2D.GetScenePosition().XY;
            float3 color = 0f;

            for (int m = 0; m < AA; m++)
            {
                for (int n = 0; n < AA; n++)
                {
                    float2 off = (new float2((float)m, (float)n) + 0.5f) / (float)AA - 0.5f;
                    float2 u = U + off;

                    // 整体流速减半：所有 t 的派生项（旋转相位、Z 轴摆动）同步降速，仅作用于本 shader。
                    float t = time * 0.5f;
                    float3 p = new float3(0f, 0f, -1f - 0.5f * Hlsl.Sin(t * 0.1f));
                    float3 d = Hlsl.Normalize(new float3(2f * u - dispatchSize, dispatchSize.Y));
                    float3 l = 0f;

                    for (float i = 0f; i < 10f; i += 1f)
                    {
                        float3 b = p;
                        b.XY = R(Hlsl.Sin(b.XY * 0.25f), t * 0.5f + b.Z * 2f);

                        float s = 0.001f + Hlsl.Abs(Noise(b * 20f) / 20f - Noise(b)) * 0.7f;
                        s += Hlsl.Abs(p.Y * 0.2f + Hlsl.Sin(p.Z * 2f + p.X * 0.5f)) * 0.5f;

                        p += d * s;

                        float mix = Hlsl.Sin(i + Hlsl.Length(p.XY * 0.1f)) * 0.5f + 0.5f;
                        float3 stepColor = Hlsl.Lerp(
                            Hlsl.Lerp(color1, color2, mix),
                            Hlsl.Lerp(color3, color4, mix),
                            mix
                        );
                        l += stepColor * 2.5f / s;
                    }

                    color += Aces(l * l / 500f);
                }
            }

            color /= (float)(AA * AA);

            if (isDark)
            {
                float maxC = Hlsl.Max(color.X, Hlsl.Max(color.Y, color.Z));
                float knee = 0.2f;
                float ceiling = 0.6f;
                float range = ceiling - knee;
                float compressed = maxC <= knee
                    ? maxC
                    : knee + range * (1.0f - Hlsl.Exp(-(maxC - knee) / range));
                color *= compressed / maxC;
            }

            float ditherOffset = enableDithering ? (Dither(U) - 0.5f) / 255f : 0f;

            return new float4(Hlsl.Saturate(color + ditherOffset), 1f);
        }
    }
}