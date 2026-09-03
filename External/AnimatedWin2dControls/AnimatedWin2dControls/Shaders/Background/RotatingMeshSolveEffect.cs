using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// 旋转网格背景的低分辨率变形求解 pass。逐像素不动点迭代求解 <c>PinchVertex</c>
    /// 网格变形的逆（原版逐三角形精确扫描在 D2D1 中无法实现——FXC 禁止梯度指令出现
    /// 在不可展开循环内），把解出的网格 uv 写入 RGBA32F 目标（RG = uv，B = 0，A = 1）。
    ///
    /// <para>
    /// 独立成低分辨率 pass 的依据：被求解的变形场是 from/to 网格（竖屏 21×21 /
    /// 横屏 33×33）逐顶点按 <c>pinchMix</c> 混合后的分片双线性插值，空间频率被网格
    /// 分辨率截断——1/4 分辨率下每个网格单元仍有 8 个以上采样点，硬件双线性重建
    /// 与逐像素全分辨率求解无可感知差异（内容本身还叠加 σ≈170px 的模糊）。
    /// 由此 26 次迭代的成本从全屏像素量降至 1/16，求解质量不回退；若留在合成
    /// pass 内做全分辨率迭代，该 pass 将主导整条管线的 GPU 开销。
    /// </para>
    /// <para>
    /// 输出经渲染器上采样为合成 pass 的输入 1（网格 uv 场）；折叠域内不收敛的
    /// 像素按残差软混合回落到未变形采样，全程连续无硬边。
    /// </para>
    /// </summary>
    [D2DInputCount(1)]
    [D2DInputComplex(0)]
    [D2DInputDescription(0, D2D1Filter.MinMagMipLinear)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct RotatingMeshSolveEffect(
        float2 dispatchSize,
        float pinchMix,
        int meshRows,
        int meshColumns) : ID2D1PixelShader
    {
        /// <summary>
        /// 软覆盖混合斜率：残差小于 1/(4·slope) 的像素完全贴合变形采样。迭代收敛
        /// 后残差只剩 ~10⁻³·位移量，此兜底仅在强剪切病态域（求解不收敛处）生效，
        /// 不会削弱正常区域的变形强度。
        /// </summary>
        private const float CoverageBlendSlope = 4f;

        public float4 Execute()
        {
            float2 scene = D2D.GetScenePosition().XY;
            float2 uv = scene / dispatchSize;

            // 屏幕 uv（左上原点）转 clip space（D3D NDC +Y 朝上），逐像素求解
            // 网格变形的逆，结果以 texel 精度写入目标供合成 pass 双线性重建。
            float2 ndc = new float2(uv.X * 2f - 1f, 1f - uv.Y * 2f);
            float2 meshUv = SolveMeshUv(ndc);

            return new float4(meshUv.X, meshUv.Y, 0f, 1f);
        }

        /// <summary>
        /// 逐像素不动点求解网格变形，返回混合后的采样 uv：变形为 from/to 网格逐顶点
        /// 按 <paramref name="pinchMix"/> 混合后的分片双线性插值。网格近似恒等变形，
        /// 屏幕 uv 是很好的迭代初值；折叠域内不收敛的像素按残差软混合回落到
        /// 未变形采样（原版为逐三角形精确扫描），全程连续无硬边。
        /// </summary>
        private float2 SolveMeshUv(float2 ndc)
        {
            float2 screenUv = new float2(0.5f * (ndc.X + 1f), 0.5f * (1f - ndc.Y));
            float2 uv = screenUv;

            // 固定次数阻尼不动点迭代、无 break：FXC 禁止梯度指令（Sample）
            // 出现在迭代次数不确定的循环内。迭代 x += α(ndc - Warp(x)) 的收敛域
            // 是变形雅可比特征值 ∈ (0, 2/α)：α = 0.7 时位移拉伸梯度上限 ≈1.86
            // （α = 0.9 时只有 ≈1.22），足以覆盖放大网格的强剪切区；稳定域内
            // 收缩率 ≤ ~0.75，26 次后残差 ≈ 10⁻³·初始位移，变形全强度呈现。
            // 相比牛顿法，不动点迭代不含雅可比计算与任何分支——求解结果像素
            // 连续，折叠域表现为平滑拉伸而非按网格单元碎裂的硬边鬼影。
            for (int iteration = 0; iteration < 26; iteration++)
            {
                float2 warp = Warp(uv);
                uv = Hlsl.Clamp(uv + (ndc - warp) * 0.7f, 0f, 1f);
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
        /// 插值（即 <c>PinchVertex</c>）。网格数据按数组行序存于输入 0（纹理顶行 =
        /// 数组 row 0 = NDC 底部），格点位于纹素中心，硬件双线性即格点插值。
        /// </summary>
        private float2 Warp(float2 uv)
        {
            float gridX = Hlsl.Clamp(uv.X * (meshColumns - 1), 0f, meshColumns - 1f);
            float gridY = Hlsl.Clamp((1f - uv.Y) * (meshRows - 1), 0f, meshRows - 1f);

            float4 vertex = D2D.SampleInput(0, new float2(
                (gridX + 0.5f) / meshColumns,
                (gridY + 0.5f) / meshRows));

            return new float2(
                Hlsl.Lerp(vertex.X, vertex.Z, pinchMix),
                Hlsl.Lerp(vertex.Y, vertex.W, pinchMix));
        }
    }
}
