using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// 移植自 <see href="https://github.com/ghost1372/DevWinUI/blob/main/dev/DevWinUI.Shader/Shaders/PS3XMBBackgroundShader.cs"/>
    /// (原作出处 <see href="https://www.shadertoy.com/view/fcf3Dn"/>)。
    /// 原版硬编码 4 个 lerp 端点 (l1..l4) 拉成 color1..4 颜色参数，raymarch 主体逻辑保持不变。
    /// 本地另对 4 色梯度 lerp 加了"呼吸"复合 warp：先按宽高比校正后的 ±60° 双向 sin 旋转（k=0.3，~21s 周期），
    /// 再叠加低速正弦漂移（速度 time*1.5f，振幅 1/30 与 1/60）。
    /// raymarch 主体与灰尘层（GetDust）保持原状，签名未变。
    /// </summary>
    [D2DInputCount(0)]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    [D2DRequiresScenePosition]
    public readonly partial struct PS3XMBEffect(
        float time,
        float2 dispatchSize,
        float3 color1,
        float3 color2,
        float3 color3,
        float3 color4,
        bool enableDithering = true) : ID2D1PixelShader
    {
        private const float THRESHOLD = 0.99f;
        private const float MIN_DIST = 0.04f;
        private const float MAX_DIST = 40f;

        private static float Hash12(float2 p)
        {
            UInt2 q = new UInt2((uint)(int)p.X, (uint)(int)p.Y) * new UInt2(1597334673u, 3812015801u);
            uint n = (q.X ^ q.Y) * 1597334673u;
            return (float)n * 2.328306437080797e-10f;
        }

        private static float Value2D(float2 p)
        {
            float2 pg = Hlsl.Floor(p);
            float2 pc = p - pg;
            pc = pc * pc * (3f - 2f * pc);

            return Hlsl.Lerp(
                Hlsl.Lerp(Hash12(pg + new float2(0, 0)), Hash12(pg + new float2(1, 0)), pc.X),
                Hlsl.Lerp(Hash12(pg + new float2(0, 1)), Hash12(pg + new float2(1, 1)), pc.X),
                pc.Y
            );
        }

        private static float GetStarsRough(float2 p)
        {
            float s = Hlsl.SmoothStep(THRESHOLD, 1f, Hash12(p));

            if (s >= THRESHOLD)
            {
                float v = (s - THRESHOLD) / (1f - THRESHOLD);
                v = Hlsl.Clamp(v, 0f, 1f);
                s = Hlsl.Pow(v, 10f);
            }

            return s;
        }

        private static float GetStars(float2 p, float a, float t, float time)
        {
            float2 pg = Hlsl.Floor(p);
            float2 pc = p - pg;
            pc = pc * pc * (3f - 2f * pc);

            float s = Hlsl.Lerp(
                Hlsl.Lerp(GetStarsRough(pg), GetStarsRough(pg + new float2(1, 0)), pc.X),
                Hlsl.Lerp(GetStarsRough(pg + new float2(0, 1)), GetStarsRough(pg + new float2(1, 1)), pc.X),
                pc.Y
            );

            float anim = Value2D(p * 0.1f + new float2(time, time));
            anim = Hlsl.Clamp(anim, 0f, 1f);
            anim = anim * 0.5f + 0.5f;

            return Hlsl.SmoothStep(a, a + t, s) * Hlsl.Pow(anim, 8.3f);
        }

        private static float GetDust(float2 p, float2 size, float f, float time, float2 dispatchSizeLocal)
        {
            float2 ar = new float2(dispatchSizeLocal.X / dispatchSizeLocal.Y, 1f);

            float2 pp = p * size * ar;

            float w = Hlsl.Pow(0.64f + 0.46f * Hlsl.Cos(p.X * 6.2831f), 1.7f);

            float s1 = GetStars(0.1f * pp + time * new float2(20f, -10.1f), 0.11f, 0.71f, time);
            float s2 = GetStars(0.2f * pp + time * new float2(30f, -10.1f), 0.1f, 0.31f, time);
            float s3 = GetStars(0.32f * pp + time * new float2(40f, -10.1f), 0.1f, 0.91f, time);

            return w * f * (s1 * 4f + s2 * 5f + s3 * 2f);
        }

        private static float Sdf(float3 p, float time)
        {
            p *= 2f;

            float o =
                8.2f * Hlsl.Sin(0.05f * p.X + time * 0.25f) +
                (0.04f * p.Z) *
                Hlsl.Sin(p.X * 0.11f + time) *
                2f * Hlsl.Sin(p.Z * 0.2f + time) *
                Value2D(new float2(0.03f, 0.4f) * p.XZ + new float2(time * 0.5f, 0f));

            return Hlsl.Abs(Hlsl.Dot(p, new float3(0f, 1f, 0.05f)) + 2.5f + o * 0.5f);
        }

        private static float2 RayMarch(float3 o, float3 d, float jitter, float time)
        {
            float t = jitter * 2f;
            float a = 0f;
            float g = MAX_DIST;
            int dr = 0;

            for (int i = 0; i < 100; i++)
            {
                float3 p = o + d * t;
                float ndt = Sdf(p, time);

                g = t > 10f ? Hlsl.Min(g, Hlsl.Abs(ndt)) : MAX_DIST;

                if (t >= MAX_DIST) break;

                if (Hlsl.Abs(ndt) < MIN_DIST)
                {
                    if (dr > 40) break;
                    dr++;

                    float f = Hlsl.SmoothStep(0f, 0.3f, (p.Z * 0.9f) / 100f);

                    a += 0.015f * f;
                    t += 0.05f;
                }
                else
                {
                    t += Hlsl.Abs(ndt) * 0.8f;
                }
            }

            return new float2(a, Hlsl.Clamp(1f - g / 3f, 0f, 1f));
        }

        private static float Dither(float2 pos)
        {
            return Hlsl.Frac(52.9829189f * Hlsl.Frac(Hlsl.Dot(pos, new float2(0.06711056f, 0.00583715f))));
        }

        public float4 Execute()
        {
            float2 U = D2D.GetScenePosition().XY;
            float2 ires = dispatchSize;

            float3 o = 0f;
            float3 d = new float3((U - 0.5f * ires) / ires.Y, 1f);

            float2 mg = RayMarch(o, d, Dither(U), time);
            float m = mg.X;

            float2 uv = U / ires;

            // 梯度"慢呼吸"：ratio 校正仅包裹旋转，drift 保持在原屏幕坐标
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

            float3 c = Hlsl.Lerp(
                Hlsl.Lerp(color1, color2, tuv.X),
                Hlsl.Lerp(color3, color4, tuv.X),
                tuv.Y
            );

            c = Hlsl.Lerp(c, 1f, Hlsl.Clamp(m, 0f, 1f));

            c += GetDust(uv, new float2(2000f, 2000f), mg.Y, time, ires) * 0.3f;

            float ditherOffset = enableDithering ? (Dither(U) - 0.5f) / 255f : 0f;

            return new float4(Hlsl.Saturate(c + ditherOffset), 1f);
        }
    }
}
