using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// Apple Music 风格背景的最终合成 pass。移植自 Lyricify-Backgrounds 的
    /// <c>MaterialTreatedPixel</c> / <c>PinchVertex</c> / <c>PinchPixel</c> /
    /// <c>FinishMaterial</c>（Apache 2.0），经 ComputeSharpDemo 的 compute 版本
    /// 转写为 D2D1 像素着色器。
    ///
    /// <para>
    /// 输入 0 为经原生 <c>GaussianBlurEffect</c>（Soft 边框）模糊后的旋转封面层，
    /// premultiplied 存储：对软边框模糊结果做 un-premultiply（除以覆盖率），
    /// 与原版"零边框采样 + 按累计 alpha 归一化"的结果完全一致。
    /// </para>
    /// <para>
    /// 输入 1 为 pinch 网格纹理（RGBA32F：RG = from 网格 NDC 坐标，BA = to 网格
    /// NDC 坐标，格点位于纹素中心）。线性滤波下的硬件双线性即逐顶点混合的精确
    /// 等价形式（lerp 与双线性可交换），故 <c>Warp(uv)</c> 只需一次采样。
    /// 网格变形经牛顿迭代逐像素反向求解；原版对未收敛像素的逐三角形扫描在 D2D1
    /// 中无法实现（FXC 禁止梯度指令出现在不可展开循环内），此处以残差软混合近似：
    /// 求解越好越贴近变形采样，折叠域内平滑回落到未变形采样，避免硬边鬼影。
    /// </para>
    /// </summary>
    [D2DInputCount(2)]
    [D2DInputComplex(0)]
    [D2DInputComplex(1)]
    [D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
    [D2DInputDescription(1, D2D1Filter.MinMagMipLinear)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct AppleMusicCompositeEffect(
        float2 dispatchSize,
        float pinchMix,
        float3 scrimColor,
        float scrimAlpha,
        float ditherStrength,
        float pinchTextureScale,
        float pinchTextureOffset,
        int meshRows,
        int meshColumns) : ID2D1PixelShader
    {
        /// <summary>软覆盖混合斜率：NDC 残差达到 1/20 时完全回落到未变形采样。</summary>
        private const float CoverageBlendSlope = 8f;

        public float4 Execute()
        {
            float2 scene = D2D.GetScenePosition().XY;
            float2 uv = scene / dispatchSize;

            // pinch 网格（PinchVertex + PinchPixel），逐像素反向求解变形。
            // 求解结果永远有效（软覆盖混合），无需回退分支。
            float2 ndc = new float2(uv.X * 2f - 1f, 1f - uv.Y * 2f);
            float2 meshUv = SolveMeshUv(ndc);

            float2 textureCoordinate = new float2(
                meshUv.X * pinchTextureScale + pinchTextureOffset,
                meshUv.Y * pinchTextureScale + pinchTextureOffset);

            float3 color = SampleTreatedMaterial(textureCoordinate);

            color = FinishMaterial(color, scene);

            return new float4(Hlsl.Saturate(color.X), Hlsl.Saturate(color.Y), Hlsl.Saturate(color.Z), 1f);
        }

        /// <summary>
        /// 逐像素牛顿求解网格变形，返回混合后的采样 uv：变形为 from/to 网格逐顶点
        /// 按 <paramref name="pinchMix"/> 混合后的分片双线性插值。网格近似恒等变形，
        /// 屏幕 uv 是很好的迭代初值；折叠域内不收敛的像素按残差软混合回落到
        /// 未变形采样（原版为逐三角形精确扫描），全程连续无硬边。
        /// </summary>
        private float2 SolveMeshUv(float2 ndc)
        {
            float2 screenUv = new float2(0.5f * (ndc.X + 1f), 0.5f * (1f - ndc.Y));
            float2 uv = screenUv;

            // 固定 12 次阻尼不动点迭代、无 break：FXC 禁止梯度指令（Sample）
            // 出现在迭代次数不确定的循环内。相比牛顿法，不动点迭代不含雅可比
            // 计算与任何分支——求解结果像素连续，折叠域表现为平滑拉伸而非
            // 按网格单元碎裂的硬边鬼影。网格近似恒等变形，收敛迅速。
            for (int iteration = 0; iteration < 16; iteration++)
            {
                float2 warp = Warp(uv);
                uv = Hlsl.Clamp(uv + (ndc - warp) * 0.9f, 0f, 1f);
            }

            float2 final = Warp(uv);
            float residual = Hlsl.Length(new float2(final.X - ndc.X, final.Y - ndc.Y));

            // 软覆盖：残差越大越回落到未变形采样。折叠域内 original 走逐三角形
            // 精确扫描；此处以平滑混合近似，消除"几何鬼影"式的硬边内容错位。
            float coverage = Hlsl.Saturate(1f - residual * CoverageBlendSlope);

            return Hlsl.Lerp(screenUv, Hlsl.Clamp(uv, 0f, 1f), coverage);
        }

        /// <summary>
        /// 正向变形：from/to 网格逐顶点按 <paramref name="pinchMix"/> 混合后的双线性
        /// 插值（即 <c>PinchVertex</c>）。网格数据按数组行序存于输入 1（纹理顶行 =
        /// 数组 row 0 = NDC 底部），格点位于纹素中心，硬件双线性即格点插值。
        /// </summary>
        private float2 Warp(float2 uv)
        {
            float gridX = Hlsl.Clamp(uv.X * (meshColumns - 1), 0f, meshColumns - 1f);
            float gridY = Hlsl.Clamp((1f - uv.Y) * (meshRows - 1), 0f, meshRows - 1f);

            float4 vertex = D2D.SampleInput(1, new float2(
                (gridX + 0.5f) / meshColumns,
                (gridY + 0.5f) / meshRows));

            return new float2(
                Hlsl.Lerp(vertex.X, vertex.Z, pinchMix),
                Hlsl.Lerp(vertex.Y, vertex.W, pinchMix));
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
            // 最终合成前先做饱和度调整。
            color = ApplySaturation(color, 1.4f);
            color = new float3(
                Hlsl.Clamp(color.X, -0.752941f, 1.25098f),
                Hlsl.Clamp(color.Y, -0.752941f, 1.25098f),
                Hlsl.Clamp(color.Z, -0.752941f, 1.25098f));
            color = ApplySaturation(color, 0.70f);

            // 黑/白 scrim：暗色歌词主题压黑，亮色主题提白（原版为黑色 scrim）。
            return Hlsl.Lerp(color, scrimColor, scrimAlpha);
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
