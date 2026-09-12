using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// 旋转网格背景的最终合成 pass。移植自 Lyricify-Backgrounds 的
    /// <c>MaterialTreatedPixel</c> / <c>PinchPixel</c> / <c>FinishMaterial</c>
    /// （Apache 2.0），经 ComputeSharpDemo 的 compute 版本转写为 D2D1 像素着色器。
    ///
    /// <para>
    /// 输入 0 为经原生 <c>GaussianBlurEffect</c>（Soft 边框）模糊后的旋转封面层，
    /// premultiplied 存储：对软边框模糊结果做 un-premultiply（除以覆盖率），
    /// 与原版"零边框采样 + 按累计 alpha 归一化"的结果完全一致。
    /// </para>
    /// <para>
    /// 输入 1 为 <see cref="RotatingMeshSolveEffect"/> 预求解的网格 uv 场
    /// （全分辨率 RGBA32F：RG = 最终材质 uv）。折叠和覆盖边界上的 uv 不连续，
    /// 必须以相同像素尺寸、Point 采样读取，避免跨网格片插值形成锯齿状鬼影。
    /// </para>
    /// </summary>
    [D2DInputCount(2)]
    [D2DInputComplex(0)]
    [D2DInputComplex(1)]
    [D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
    [D2DInputDescription(1, D2D1Filter.MinMagMipPoint)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct RotatingMeshCompositeEffect(
        float2 dispatchSize,
        bool isDark,
        float lumaStrength,
        float ditherStrength) : ID2D1PixelShader
    {
        public float4 Execute()
        {
            float2 scene = D2D.GetScenePosition().XY;
            float2 uv = scene / dispatchSize;

            // 同分辨率逐像素 UV；折叠两侧属于不同的网格片，不能插值。
            float2 meshUv = D2D.SampleInput(1, uv).XY;

            float3 color = SampleTreatedMaterial(meshUv);

            color = FinishMaterial(color, scene);

            return new float4(Hlsl.Saturate(color.X), Hlsl.Saturate(color.Y), Hlsl.Saturate(color.Z), 1f);
        }

        /// <summary>
        /// 对 premultiplied 模糊结果 un-premultiply（等价原版零边框覆盖率归一化），
        /// 再应用材质处理。
        /// </summary>
        private float3 SampleTreatedMaterial(float2 uv)
        {
            float4 sample = D2D.SampleInput(0, uv);
            float alpha = Hlsl.Max(sample.W, 1f / 65535f);

            return ApplyTreatedMaterial(new float3(sample.X / alpha, sample.Y / alpha, sample.Z / alpha));
        }

        /// <summary>背景材质调优的饱和度矩阵。</summary>
        private static float3 ApplySaturation(float3 color, float saturation)
        {
            float3 redColumn = new float3(
                0.2126f + 0.7873f * saturation,
                0.2126f - 0.2126f * saturation,
                0.2126f - 0.2126f * saturation);

            float3 greenColumn = new float3(
                0.7152f - 0.7152f * saturation,
                0.7152f + 0.2848f * saturation,
                0.7152f - 0.7152f * saturation);

            float3 blueColumn = new float3(
                0.0722f - 0.0722f * saturation,
                0.0722f - 0.0722f * saturation,
                0.0722f + 0.9278f * saturation);

            return new float3(
                redColumn.X * color.X + greenColumn.X * color.Y + blueColumn.X * color.Z,
                redColumn.Y * color.X + greenColumn.Y * color.Y + blueColumn.Y * color.Z,
                redColumn.Z * color.X + greenColumn.Z * color.Y + blueColumn.Z * color.Z);
        }

        private float3 ApplyTreatedMaterial(float3 color)
        {
            // 模糊会稀释色度，先做一次饱和度补偿（原 1.4/0.7 双 pass 是
            // 为配合 scrim 的去饱和而设，改为保色度的亮度轴映射后收敛为单 pass）。
            color = ApplySaturation(color, 1.6f);
            color = new float3(
                Hlsl.Clamp(color.X, -0.752941f, 1.25098f),
                Hlsl.Clamp(color.Y, -0.752941f, 1.25098f),
                Hlsl.Clamp(color.Z, -0.752941f, 1.25098f));

            // 主题适配：只重映射亮度轴，色度向量（color - luma）原样保留。
            // 原黑/白 scrim 的线性混合会把色度按 (1-α) 一并稀释——暗色发灰、
            // 亮色浮白；此处在色域内严格保色相保色度，仅越界像素等比收拢。
            float luma = Hlsl.Dot(color, new float3(0.2126f, 0.7152f, 0.0722f));
            float newLuma = isDark
                ? luma * (1f - lumaStrength)
                : Hlsl.Lerp(luma, 1f, lumaStrength);

            return ShiftLuma(color, newLuma);
        }

        /// <summary>
        /// 亮度轴重映射：把 color 的亮度分量替换为 <paramref name="newLuma"/>，
        /// 色度向量保持不变；结果越出 [0,1] 色域时沿原色相等比收拢 chroma
        /// （只裁色域外像素，不整体冲淡）。
        /// </summary>
        private static float3 ShiftLuma(float3 color, float newLuma)
        {
            float luma = Hlsl.Dot(color, new float3(0.2126f, 0.7152f, 0.0722f));
            float3 chroma = color - luma;

            float maxChroma = Hlsl.Max(chroma.X, Hlsl.Max(chroma.Y, chroma.Z));
            float minChroma = Hlsl.Min(chroma.X, Hlsl.Min(chroma.Y, chroma.Z));

            float scale = 1f;
            if (maxChroma > 0f) scale = Hlsl.Min(scale, (1f - newLuma) / maxChroma);
            if (minChroma < 0f) scale = Hlsl.Min(scale, newLuma / -minChroma);

            return newLuma + chroma * Hlsl.Saturate(scale);
        }

        /// <summary>
        /// <c>FinishMaterial</c>：半 LSB 噪声抑制量化色带。
        /// </summary>
        private float3 FinishMaterial(float3 color, float2 pixelPosition)
        {
            float dither = Hlsl.Frac(
                52.9829189f * Hlsl.Frac(Hlsl.Dot(pixelPosition, new float2(0.06711056f, 0.00583715f)))) - 0.5f;

            float strength = ditherStrength / 255f;

            return new float3(
                Hlsl.Clamp(color.X + dither * strength, 0.07f, 0.97f),
                Hlsl.Clamp(color.Y + dither * strength, 0.07f, 0.97f),
                Hlsl.Clamp(color.Z + dither * strength, 0.07f, 0.97f));
        }
    }
}
