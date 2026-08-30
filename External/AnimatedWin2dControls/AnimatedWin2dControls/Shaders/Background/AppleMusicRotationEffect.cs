using ComputeSharp;
using ComputeSharp.D2D1;

namespace AnimatedWin2dControls.Shaders.Background
{
    /// <summary>
    /// Apple Music 风格背景的旋转封面图层 pass。移植自 Lyricify-Backgrounds 的
    /// <c>RotationVertex</c> / <c>ArtworkFillVertex</c> / <c>RotationPixel</c>（Apache 2.0），
    /// 经 ComputeSharpDemo 的 compute 版本转写为 D2D1 像素着色器。
    ///
    /// <para>
    /// 原版在顶点/像素管线上光栅化四个四边形：底部一张 aspect-fill 封面 +
    /// 三层旋转封面（iOS 16.3 RotatingArtworkRenderer：model 0 scale 1.4 原点，
    /// model 1/2 scale 0.7 偏移，model 2 跟随 model 0 旋转）。D2D1 无顶点着色器，
    /// 因此逐像素反向运行变换链：每个旋转四边形的变换均为仿射，逆变换精确，
    /// 命中判定即 [-1,1] 方形测试。图层按最上层优先（instance 2 → 1 → 0）求值，
    /// 与原绘制顺序一致，未命中回落到 aspect-fill 层。
    /// </para>
    /// <para>
    /// 封面通过 <see cref="D2D1ResourceTexture2D{Float4}"/> 注入，采样器在
    /// D2D1ResourceTextureManager 初始化时配置为线性 + Clamp，等价原版的
    /// LinearClampSampler。输出恒不透明（alpha=1），可直接写入 premultiplied 目标。
    /// </para>
    /// </summary>
    [D2DInputCount(0)]
    [D2DRequiresScenePosition]
    [D2DShaderProfile(D2D1ShaderProfile.PixelShader50)]
    [D2DGeneratedPixelShaderDescriptor]
    public readonly partial struct AppleMusicRotationEffect(
        float2 dispatchSize,
        float time,
        float rotationScale,
        float imageScale,
        float artworkMix) : ID2D1PixelShader
    {
        private const float TwoPi = 6.2831853071795864769f;

        /// <summary>
        /// RGBA8 封面纹理（非预乘，管理器侧配置线性 + Clamp 采样）。
        /// 双纹理用于换歌时的像素级交叉淡化：A=上一张，B=当前张，artworkMix 由
        /// 渲染器随时间 0→1 推进。0 输入着色器的资源纹理索引可连续使用 0..N-1。
        /// </summary>
        [D2DResourceTextureIndex(0)]
        private readonly D2D1ResourceTexture2D<Float4> _artworkA;

        [D2DResourceTextureIndex(1)]
        private readonly D2D1ResourceTexture2D<Float4> _artworkB;

        public float4 Execute()
        {
            float2 scene = D2D.GetScenePosition().XY;
            float2 uv = scene / dispatchSize;

            // 屏幕 uv（左上原点）转 clip space（D3D NDC +Y 朝上）。
            float2 ndc = new float2(uv.X * 2f - 1f, 1f - uv.Y * 2f);

            // 原绘制顺序为 0,1,2 依次叠放，故 2 最上层；未命中以 alpha=0 标记。
            float4 top = SampleRotatingInstance(2, ndc);

            if (top.W > 0f)
            {
                return top;
            }

            float4 middle = SampleRotatingInstance(1, ndc);

            if (middle.W > 0f)
            {
                return middle;
            }

            float4 bottom = SampleRotatingInstance(0, ndc);

            if (bottom.W > 0f)
            {
                return bottom;
            }

            // 旋转层之下的 aspect-fill 兜底（ArtworkFillVertex）：填充四边形的
            // 纹理坐标按 viewScale 扩展出 [0,1]，保证任意纵横比下封面铺满全屏。
            // viewScale 由画面纵横比在片元内推导，与原版逐帧 CPU 计算等价。
            float aspectRatio = dispatchSize.X / dispatchSize.Y;
            float2 viewScale = aspectRatio >= 1f
                ? new float2(1f, aspectRatio)
                : new float2(1f / aspectRatio, 1f);

            float2 fillUv = new float2(
                (uv.X - 0.5f) / viewScale.X + 0.5f,
                (uv.Y - 0.5f) / viewScale.Y + 0.5f);

            return new float4(SampleArtwork(fillUv).XYZ, 1f);
        }

        /// <summary>按 artworkMix 交叉淡化两张封面。</summary>
        private float4 SampleArtwork(float2 uv)
        {
            float4 a = _artworkA.Sample(uv.X, uv.Y);
            float4 b = _artworkB.Sample(uv.X, uv.Y);

            return Hlsl.Lerp(a, b, Hlsl.Saturate(artworkMix));
        }

        /// <summary>
        /// 对单个 instance 逆向求解 <c>RotationVertex</c>。命中返回采样色（alpha=1），
        /// 未命中返回 alpha=0。
        /// </summary>
        private float4 SampleRotatingInstance(int instanceId, float2 ndc)
        {
            // 正向链（RotationVertex）：局部角度旋转 → model 缩放 → 平移 →
            // view 纵横比缩放 → model 2 叠加父级旋转 → 整体材质缩放；此处逆序求逆。
            float2 position = new float2(ndc.X / imageScale, ndc.Y / imageScale);

            if (instanceId == 2)
            {
                float parentAngle = time * rotationScale * TwoPi / RotationTimeScale(0);
                position = RotateCounterClockwise(position, -parentAngle);
            }

            float aspectRatio = dispatchSize.X / dispatchSize.Y;
            float2 viewScale = aspectRatio >= 1f
                ? new float2(1f, aspectRatio)
                : new float2(1f / aspectRatio, 1f);

            position = new float2(position.X / viewScale.X, position.Y / viewScale.Y);

            float2 translation = ModelTranslation(instanceId);
            position = new float2(position.X - translation.X, position.Y - translation.Y);

            float modelScale = ModelScale(instanceId);
            position = new float2(position.X / modelScale, position.Y / modelScale);

            float angle = time * rotationScale * TwoPi / RotationTimeScale(instanceId);
            position = RotateCounterClockwise(position, -angle);

            if (Hlsl.Abs(position.X) > 1f || Hlsl.Abs(position.Y) > 1f)
            {
                return float4.Zero;
            }

            // 四边形角点映射纹理坐标 ((local.x+1)/2, (1-local.y)/2)，封面保持正立
            // （局部 y+1 = 屏幕上侧 = 纹理第 0 行）。
            float2 uv = new float2((position.X + 1f) * 0.5f, (1f - position.Y) * 0.5f);

            return new float4(SampleArtwork(uv).XYZ, 1f);
        }

        private static float2 RotateCounterClockwise(float2 value, float angle)
        {
            float sine = Hlsl.Sin(angle);
            float cosine = Hlsl.Cos(angle);

            return new float2(
                cosine * value.X - sine * value.Y,
                sine * value.X + cosine * value.Y);
        }

        // iOS 16.3 构造参数：model 0 = scale 1.4，model 1/2 = scale 0.7。
        private static float ModelScale(int instanceId)
        {
            return instanceId == 0 ? 1.4f : 0.7f;
        }

        private static float2 ModelTranslation(int instanceId)
        {
            if (instanceId == 1)
            {
                return new float2(-0.25f, 0.15f);
            }

            if (instanceId == 2)
            {
                return new float2(0.7f, 0.7f);
            }

            return new float2(0f, 0f);
        }

        // iOS 16.3 RotatingArtworkRenderer：model 0 = 120s，model 1 = 70s，model 2 = 90s。
        private static float RotationTimeScale(int instanceId)
        {
            if (instanceId == 1)
            {
                return 70f;
            }

            if (instanceId == 2)
            {
                return 90f;
            }

            return 120f;
        }

    }
}
