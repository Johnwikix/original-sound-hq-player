using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using System;
using Windows.Graphics.DirectX;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// 旋转网格背景渲染器。移植自 Lyricify-Backgrounds（Apache 2.0）经
    /// ComputeSharpDemo 转写的 compute 版本，适配本项目 D2D1 像素着色器管线：
    ///
    /// <list type="number">
    /// <item><see cref="RotatingMeshRotationEffect"/> —— 三层旋转封面 + aspect-fill
    /// 兜底，封面经双 CanvasBitmap 输入（RGBA8，线性 + Clamp 描述）注入并可显式
    /// Dispose，绘制到 1/8 像素密度的中间目标（对应原版 1/7.53 背景面）。</item>
    /// <item>原生 <see cref="GaussianBlurEffect"/>（Soft 边框）做 77 抽头 σ 可分离
    /// 高斯模糊的等价实现：premultiplied 软边框模糊 + 合成 pass 内 un-premultiply，
    /// 与原版"零边框采样 + 覆盖率归一化"逐像素一致。</item>
    /// <item><see cref="RotatingMeshCompositeEffect"/> —— 材质处理 + pinch 网格逆向
    /// 变形 + 抖动，网格顶点经 CanvasBitmap（RGBA32F，输入 1）注入，输出到全屏。
    /// 注：ComputeSharp.D2D1 3.2.0 的资源纹理管理器仅适用于无输入的着色器
    /// （属性槽按声明索引线性映射且只注册 Count 个，与输入占用的寄存器约束冲突），
    /// 故合成 pass 的网格改走效果输入。</item>
    /// </list>
    ///
    /// <para>
    /// 封面适配：原 demo 从固定路径读图，本渲染器改由 <see cref="SetArtwork"/> 注入
    /// 缩略图缓存解码出的方形 RGBA8（见 <see cref="ArtworkPixelDecoder"/>）；无封面时
    /// 退化为调色板四色渐变或默认深色渐变，保证选歌前也有完整视觉。HDR/PQ 编码与
    /// 频谱缩放、封面交叉淡化（demo 亦未实现）不适用 SDR 画布，予以省略。
    /// </para>
    /// </summary>
    public sealed class RotatingMeshBackgroundRenderer : BaseBackgroundRenderer
    {
        /// <summary>中间旋转/模糊层的像素密度（1/8，对应原版 backdropDownsample≈7.53）。</summary>
        private const float BackdropPixelScale = 1f / 8f;

        /// <summary>
        /// 中间层上的高斯 σ。原版 σ_uv = 170/输出宽，映射到 1/8 目标即 21.25px，
        /// 与分辨率无关（BlurAmount 以 96DPI 目标的 DIP 计，此处 DIP=px）。
        /// </summary>
        private const float BackdropBlurSigma = 21.25f;

        private const float MeshWarpTimeScale = 5f;
        private const float DarkScrimAlpha = 0.4f;
        private const float LightScrimAlpha = 0.45f;
        private const float PortraitTextureScale = 1f;
        private const float LandscapeTextureScale = 0.8f;

        /// <summary>pinch 网格位移放大倍数：绕恒等网格线性扩偏移，增强形变可见度。</summary>
        private const float MeshWarpStrength = 2.0f;

        /// <summary>换歌封面交叉淡化时长（秒）。</summary>
        private const float ArtworkTransitionDuration = 0.8f;

        // 保护效果/中间目标生命周期：Dispose/LoadResources 持锁，Draw 走 TryEnter(0)
        // 抢不到就丢一帧，渲染线程永不阻塞（与 PS3XMB 渲染器一致）。
        private readonly object _gate = new();

        private PixelShaderEffect<RotatingMeshRotationEffect>? _rotationEffect;
        private PixelShaderEffect<RotatingMeshCompositeEffect>? _compositeEffect;
        private GaussianBlurEffect? _blurEffect;
        private ScaleEffect? _scaleEffect;
        private CanvasRenderTarget? _rotationTarget;
        private CanvasRenderTarget? _blurTarget;
        private CanvasRenderTarget? _upscaledTarget;
        private int _targetWidth;
        private int _targetHeight;
        private float _targetDpi;
        private float _meshDpi;

        // 双槽封面位图：A/B 各一张（128×128，96 DPI），换歌时新封面写入非活动槽并
        // 交叉淡化。与网格一致走效果输入，可对旧图显式 Dispose（无终结器延迟）。
        private readonly CanvasBitmap?[] _coverBitmaps = new CanvasBitmap?[2];
        private int _activeArtworkSlot;
        private float _artworkTransitionStart;
        private bool _artworkTransitioning;
        private CanvasBitmap? _meshBitmap;
        private bool _meshIsPortrait;
        private int _meshRows;
        private int _meshColumns;

        private readonly int _presetSlot = RotatingMeshWarp.SelectPresetSlot();

        // 渲染线程每帧在锁内取走并推入封面位图；volatile 保证 UI 线程写入的可见性
        // （引用赋值原子 + 单写单消费，配合 volatile 无丢失更新）。
        private volatile ArtworkPixelData? _pendingArtwork;
        private ArtworkPixelData? _realArtwork;

        public override void LoadResources()
        {
            lock (_gate)
            {
                _rotationTarget?.Dispose();
                _rotationTarget = null;
                _blurTarget?.Dispose();
                _blurTarget = null;
                _upscaledTarget?.Dispose();
                _upscaledTarget = null;
                _blurEffect?.Dispose();
                _blurEffect = null;
                _scaleEffect?.Dispose();
                _scaleEffect = null;
                _meshBitmap?.Dispose();
                _meshBitmap = null;
                _coverBitmaps[0]?.Dispose();
                _coverBitmaps[1]?.Dispose();
                _coverBitmaps[0] = null;
                _coverBitmaps[1] = null;

                // 效果绑定旧设备的已实现资源，设备重建后必须整体重建；
                // 位图输入为设备绑定资源，重建后由 Draw 惰性重创建并重新绑定。
                _rotationEffect?.Dispose();
                _rotationEffect = new PixelShaderEffect<RotatingMeshRotationEffect>();
                _compositeEffect?.Dispose();
                _compositeEffect = new PixelShaderEffect<RotatingMeshCompositeEffect>();
            }

            if (CurrentPalette is not null) SetPalette(CurrentPalette);
            else ApplyDefaultColors();
            SnapToTarget();
        }

        public override void Update(TimeSpan deltaTime)
        {
            UpdateBreathing(0f, 0);
            Advance(deltaTime);
        }

        public override void Draw(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            if (!Monitor.TryEnter(_gate, 0)) return;
            try
            {
                var compositeEffect = _compositeEffect;
                if (compositeEffect == null || Opacity <= 0) return;

                float widthDip = (float)control.Size.Width;
                float heightDip = (float)control.Size.Height;
                if (widthDip <= 0f || heightDip <= 0f) return;

                float pixelWidth = control.ConvertDipsToPixels(widthDip, CanvasDpiRounding.Round);
                float pixelHeight = control.ConvertDipsToPixels(heightDip, CanvasDpiRounding.Round);
                int backdropWidth = Math.Max(1, (int)MathF.Round(pixelWidth * BackdropPixelScale));
                int backdropHeight = Math.Max(1, (int)MathF.Round(pixelHeight * BackdropPixelScale));

                EnsureTargets(control, widthDip, heightDip, backdropWidth, backdropHeight);
                EnsureMeshBitmap(control, backdropHeight > backdropWidth);
                EnsureCoverBitmaps(control);
                PushPendingArtwork(control);

                // PinchVertex：phase = acos(sin(Time * pi / 5)) / pi，mix = smoothstep(phase)。
                float time = Time;
                float phase = MathF.Acos(MathF.Sin(time * MathF.PI / MeshWarpTimeScale)) / MathF.PI;
                float pinchMix = phase * phase * (3f - 2f * phase);

                bool isPortrait = backdropHeight > backdropWidth;
                float pinchTextureScale = isPortrait ? PortraitTextureScale : LandscapeTextureScale;
                float pinchTextureOffset = (1f - pinchTextureScale) * 0.5f;

                // 换歌交叉淡化进度：0.8s smoothstep 推向当前封面槽位。
                // 过渡期间到达的新封面保留在 _pendingArtwork，完成帧立即接续，
                // 避免连续快速切歌时覆盖"正在淡入的槽"导致画面跳变。
                float artworkMix;
                if (_artworkTransitioning)
                {
                    float t = Math.Clamp((time - _artworkTransitionStart) / ArtworkTransitionDuration, 0f, 1f);
                    if (t >= 1f)
                    {
                        _artworkTransitioning = false;
                        PushPendingArtwork(control);

                        // 有积压封面：已接续新过渡，t 归零（起点显示上一阶段终点画面）。
                        // 无积压：保留 t=1（终点画面），避免完成帧 mix 跳回旧封面闪一帧。
                        t = _artworkTransitioning ? 0f : 1f;
                    }

                    artworkMix = t * t * (3f - 2f * t);
                    if (_activeArtworkSlot == 0) artworkMix = 1f - artworkMix;
                }
                else
                {
                    artworkMix = _activeArtworkSlot == 1 ? 1f : 0f;
                }

                // Pass 1 —— 旋转封面层绘制到 1/8 中间目标。
                _rotationEffect!.ConstantBuffer = new RotatingMeshRotationEffect(
                    new float2(_targetWidth, _targetHeight),
                    time,
                    rotationScale: 1f,
                    imageScale: 1f,
                    artworkMix);

                using (var rotationSession = _rotationTarget!.CreateDrawingSession())
                {
                    rotationSession.DrawImage(_rotationEffect);
                }

                // Pass 2 —— 原生高斯模糊（Soft 边框 ≡ 原版零边框 + 覆盖率归一化）。
                using (var blurSession = _blurTarget!.CreateDrawingSession())
                {
                    blurSession.DrawImage(_blurEffect!);
                }

                // Pass 2.5 —— 上采样到全屏位图：合成 pass 的输入全部为普通位图，
                // 避免效果嵌效果（composite ← ScaleEffect ← ...）的图配置风险。
                using (var upscaleSession = _upscaledTarget!.CreateDrawingSession())
                {
                    upscaleSession.DrawImage(_scaleEffect!);
                }

                // Pass 3 —— 材质处理 + pinch 网格 + 抖动，输出全屏。
                // 网格纹理创建彻底失败时跳过合成，直接呈现模糊背景（保持不透明覆盖）。
                if (_meshBitmap is not null)
                {
                    _compositeEffect!.ConstantBuffer = new RotatingMeshCompositeEffect(
                        new float2(pixelWidth, pixelHeight),
                        pinchMix,
                        IsDark ? new float3(0f, 0f, 0f) : new float3(1f, 1f, 1f),
                        IsDark ? DarkScrimAlpha : LightScrimAlpha,
                        ditherStrength: 1f,
                        pinchTextureScale,
                        pinchTextureOffset,
                        _meshRows,
                        _meshColumns);

                    if (Opacity >= 1.0)
                    {
                        ds.DrawImage(compositeEffect);
                    }
                    else
                    {
                        using var opacityEffect = new OpacityEffect
                        {
                            Source = compositeEffect,
                            Opacity = (float)Opacity
                        };
                        ds.DrawImage(opacityEffect);
                    }
                }
                else
                {
                    ds.DrawImage(_upscaledTarget);
                }

                // 立即上抛本会话内延迟累积的 D2D1 错误，避免异常漂移到
                // DrawBackground 中后续渲染器的调用点，干扰定位。
                ds.Flush();
            }
            finally { Monitor.Exit(_gate); }
        }

        // ── 封面入口 ─────────────────────────────────────────────────────

        /// <summary>
        /// 注入新封面（方形 RGBA8）。传 null 表示当前曲目无封面：回退到调色板渐变，
        /// 避免上一首的封面残留在无封面的曲目上。
        /// </summary>
        public override void SetArtwork(ArtworkPixelData? artwork)
        {
            if (artwork is not null)
            {
                _realArtwork = artwork;
                _pendingArtwork = artwork;
            }
            else
            {
                _realArtwork = null;
                _pendingArtwork = BuildPaletteArtwork(CurrentPalette) ?? CreateDefaultArtwork();
            }
        }

        public override void SetPalette(PaletteResult? palette)
        {
            base.SetPalette(palette);

            // 本着色器不消费 4 色参数，但无真实封面时用调色板生成兜底渐变。
            if (_realArtwork is null)
                _pendingArtwork = BuildPaletteArtwork(palette) ?? CreateDefaultArtwork();
        }

        // ── 资源装配 ─────────────────────────────────────────────────────

        private void EnsureTargets(
            ICanvasAnimatedControl control,
            float widthDip, float heightDip,
            int backdropWidth, int backdropHeight)
        {
            float dpi = control.Dpi;

            if (_rotationTarget is not null && _targetWidth == backdropWidth
                && _targetHeight == backdropHeight && _targetDpi == dpi)
                return;

            // 注意：DPI 变化（窗口跨屏）也必须整体重建，否则中间目标与绘制会话的
            // DPI 不一致会触发 ComputeSharp 的 DPI 补偿节点，导致图配置错误。
            _rotationTarget?.Dispose();
            _blurTarget?.Dispose();
            _upscaledTarget?.Dispose();
            _blurEffect?.Dispose();
            _scaleEffect?.Dispose();

            // 显式 96 DPI：两参数构造会继承 control 的 DPI，破坏"DIP=px"假设。
            // rotation/blur 目标为 1/8 像素密度，DIP 尺寸 = 像素尺寸。
            _rotationTarget = new CanvasRenderTarget(control, backdropWidth, backdropHeight, 96f);
            _blurTarget = new CanvasRenderTarget(control, backdropWidth, backdropHeight, 96f);

            _blurEffect = new GaussianBlurEffect
            {
                Source = _rotationTarget,
                BlurAmount = BackdropBlurSigma,
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Soft,
            };

            // 上采样目标与主画布同尺寸同 DPI（与 bgCache 一致），作为合成 pass 的输入 0。
            _upscaledTarget = new CanvasRenderTarget(control, widthDip, heightDip);

            _scaleEffect = new ScaleEffect
            {
                Source = _blurTarget,
                Scale = new Vector2(widthDip / backdropWidth, heightDip / backdropHeight),
                InterpolationMode = CanvasImageInterpolation.Linear,
            };

            _compositeEffect!.Sources[0] = _upscaledTarget;
            _targetWidth = backdropWidth;
            _targetHeight = backdropHeight;
            _targetDpi = dpi;
        }

        private void EnsureMeshBitmap(ICanvasAnimatedControl control, bool isPortrait)
        {
            float dpi = control.Dpi;

            if (_meshBitmap is not null && _meshIsPortrait == isPortrait && _meshDpi == dpi)
                return;

            RotatingMeshWarp.MeshData mesh = RotatingMeshWarp.Create(
                isPortrait ? RotatingMeshWarp.ResolvePortraitPreset(_presetSlot) : _presetSlot,
                isPortrait);

            // 变形强度放大：绕恒等网格线性扩偏移；演示网格位移距恒等较远时
            // 幅度保守，放大后扭曲更接近该风格的观感。
            AmplifyMeshWarp(mesh.From, mesh.Rows, mesh.Columns);
            AmplifyMeshWarp(mesh.To, mesh.Rows, mesh.Columns);

            // 行/列单调性约束：折叠（网格片翻转）是一切求解伪影的根源——
            // 逐行走 x、逐列走 y 强制非降序后，变形在任何混合相位下都是同胚映射，
            // 求解器处处收敛，变形全强度呈现且无碎裂。lerp 保序，From/To 分别
            // 约束即可覆盖所有 pinchMix。
            EnforceGridMonotonicity(mesh.From, mesh.Rows, mesh.Columns);
            EnforceGridMonotonicity(mesh.To, mesh.Rows, mesh.Columns);

            // 打包为 RGBA32F：RG = from 网格 NDC 坐标，BA = to 网格 NDC 坐标，
            // 按数组行序写入（纹理顶行 = 数组 row 0 = NDC 底部）。
            int vertices = mesh.Rows * mesh.Columns;
            var packed = new float[vertices * 4];
            for (int i = 0; i < vertices; i++)
            {
                packed[i * 4 + 0] = mesh.From[i].X;
                packed[i * 4 + 1] = mesh.From[i].Y;
                packed[i * 4 + 2] = mesh.To[i].X;
                packed[i * 4 + 3] = mesh.To[i].Y;
            }

            _meshBitmap?.Dispose();
            _meshBitmap = null;

            try
            {
                // 必须以绘制会话的 DPI 创建：默认 96 DPI 与会话 DPI 不一致时，
                // ComputeSharp 会插入 DpiCompensation 节点，与自定义效果的
                // complex 输入组合会被 D2D1 判定为无效图（延迟到 Flush/EndDraw 抛出）。
                // 本 shader 以归一化 UV 采样网格，DPI 取值不影响采样语义。
                _meshBitmap = CanvasBitmap.CreateFromBytes(
                    control,
                    MemoryMarshal.AsBytes(new ReadOnlySpan<float>(packed)).ToArray(),
                    mesh.Columns,
                    mesh.Rows,
                    DirectXPixelFormat.R32G32B32A32Float,
                    dpi,
                    CanvasAlphaMode.Premultiplied);
            }
            catch (Exception)
            {
                // 格式不支持等异常情况下降级为 1×1 零纹理（8-bit 恒受支持）：
                // 网格求解不收敛，全屏回落到下层 treated 材质（无变形但渲染不中断）。
                // 源位绝不能为 null，否则效果图为未绑定输入（D2DERR_INVALID_GRAPH_CONFIGURATION）。
            }

            if (_meshBitmap is null)
            {
                try
                {
                    _meshBitmap = CanvasBitmap.CreateFromBytes(
                        control,
                        new byte[4],
                        1,
                        1,
                        DirectXPixelFormat.R8G8B8A8UIntNormalized,
                        dpi,
                        CanvasAlphaMode.Premultiplied);
                }
                catch (Exception) { }
            }

            _compositeEffect!.Sources[1] = _meshBitmap;
            _meshRows = mesh.Rows;
            _meshColumns = mesh.Columns;
            _meshIsPortrait = isPortrait;
            _meshDpi = dpi;
        }

        /// <summary>
        /// 绕恒等网格线性放大位移：每个顶点相对恒等网格 (u*2-1, v*2-1) 的偏移
        /// 乘 <see cref="MeshWarpStrength"/>。恒等网格处的线性位移边界可被单调
        /// 约束吸收到边界外，避免放大后贴边压缩。
        /// </summary>
        private static void AmplifyMeshWarp(System.Numerics.Vector2[] grid, int rows, int columns)
        {
            for (int r = 0; r < rows; r++)
            {
                float v = 1f - r / (float)(rows - 1);
                for (int c = 0; c < columns; c++)
                {
                    float u = c / (float)(columns - 1);
                    var identity = new System.Numerics.Vector2(u * 2f - 1f, v * 2f - 1f);
                    int i = r * columns + c;
                    grid[i] = identity + (grid[i] - identity) * MeshWarpStrength;
                }
            }
        }

        /// <summary>
        /// 网格行/列单调性约束：逐行走 x、逐列走 y 强制非降序（保留最小间隙），
        /// 消除网格片翻转（折叠）。仅修正顺序，不限制位移幅度。
        /// </summary>
        private static void EnforceGridMonotonicity(System.Numerics.Vector2[] grid, int rows, int columns)
        {
            const float MinGap = 0.02f;

            // 逐行：x 沿列索引非降序。
            for (int r = 0; r < rows; r++)
            {
                float lower = grid[r * columns].X;
                for (int c = 1; c < columns; c++)
                {
                    float x = MathF.Max(grid[r * columns + c].X, lower + MinGap);
                    grid[r * columns + c] = new(x, grid[r * columns + c].Y);
                    lower = x;
                }
            }

            // 逐列：y 沿行索引非降序（NDC y 向上）。
            for (int c = 0; c < columns; c++)
            {
                float lower = grid[c].Y;
                for (int r = 1; r < rows; r++)
                {
                    float y = MathF.Max(grid[r * columns + c].Y, lower + MinGap);
                    grid[r * columns + c] = new(grid[r * columns + c].X, y);
                    lower = y;
                }
            }
        }

        private void EnsureCoverBitmaps(ICanvasAnimatedControl control)
        {
            if (_coverBitmaps[0] is not null)
                return;

            // 设备重建（跨屏拖动 DPI 变化）后封面位图随旧设备销毁：以"当前应显示"
            // 的封面播种双槽——真实封面优先，其次尚未推入的 pending（含 LoadResources
            // → SetPalette 重新排队的调色板兜底），最后默认渐变。若仍无条件播种默认
            // 渐变，真实封面的 pending 已被消费，封面将永久丢失（模糊后即纯色）。
            var current = _realArtwork ?? _pendingArtwork
                ?? BuildPaletteArtwork(CurrentPalette) ?? CreateDefaultArtwork();
            byte[] pixels = current.Pixels;

            for (int i = 0; i < 2; i++)
            {
                _coverBitmaps[i] = CreateCoverBitmap(control, pixels);
                _rotationEffect!.Sources[i] = _coverBitmaps[i];
            }

            // 播种已呈现 pending 同一张封面时直接消费，避免重建后多跑一次同图
            // 交叉淡化（过渡期还会延迟真正的换歌推送）。
            if (_pendingArtwork == current)
                _pendingArtwork = null;

            _activeArtworkSlot = 0;
            _artworkTransitioning = false;
        }

        /// <summary>
        /// 以旋转目标会话的 DPI（96）创建封面位图：与中间目标 DPI 不一致会触发
        /// ComputeSharp 的 DPI 补偿节点，导致效果图配置错误。本 pass 以归一化 UV
        /// 采样输入，DPI 取值不影响采样语义。
        /// </summary>
        private static CanvasBitmap CreateCoverBitmap(ICanvasAnimatedControl control, byte[] pixels)
        {
            return CanvasBitmap.CreateFromBytes(
                control,
                pixels,
                ArtworkPixelData.Edge,
                ArtworkPixelData.Edge,
                DirectXPixelFormat.R8G8B8A8UIntNormalized,
                96f,
                CanvasAlphaMode.Premultiplied);
        }

        private void PushPendingArtwork(ICanvasAnimatedControl control)
        {
            var pending = _pendingArtwork;
            if (pending is null) return;

            // 过渡进行中：保留最新封面，待完成帧接续（见 Draw 中的 mix 计算）。
            if (_artworkTransitioning) return;
            _pendingArtwork = null;

            int slot = 1 - _activeArtworkSlot;

            CanvasBitmap? created;
            try
            {
                created = CreateCoverBitmap(control, pending.Pixels);
            }
            catch (Exception)
            {
                // 创建失败：保留旧槽内容与活动状态，不发散（本帧放弃本次更新）。
                return;
            }

            _coverBitmaps[slot]?.Dispose();
            _coverBitmaps[slot] = created;
            _rotationEffect!.Sources[slot] = created;

            _activeArtworkSlot = slot;
            _artworkTransitionStart = Time;
            _artworkTransitioning = true;
        }

        // ── 兜底封面 ─────────────────────────────────────────────────────

        private static ArtworkPixelData? BuildPaletteArtwork(PaletteResult? palette)
        {
            if (palette?.Palette is not { Count: > 0 })
                return null;

            var colors = palette.Palette;
            Vector3 c1 = colors[0] / 255f;
            Vector3 c2 = colors[Math.Min(1, colors.Count - 1)] / 255f;
            Vector3 c3 = colors[Math.Min(2, colors.Count - 1)] / 255f;
            Vector3 c4 = colors[Math.Min(3, colors.Count - 1)] / 255f;

            return CreateGradientArtwork(c1, c2, c3, c4);
        }

        private static ArtworkPixelData CreateDefaultArtwork()
        {
            // 深蓝灰对角渐变，无封面时的兜底氛围色。
            return CreateGradientArtwork(
                new Vector3(0.10f, 0.12f, 0.16f),
                new Vector3(0.05f, 0.06f, 0.09f),
                new Vector3(0.07f, 0.08f, 0.12f),
                new Vector3(0.03f, 0.03f, 0.05f));
        }

        private static ArtworkPixelData CreateGradientArtwork(Vector3 c1, Vector3 c2, Vector3 c3, Vector3 c4)
        {
            int edge = ArtworkPixelData.Edge;
            var pixels = new byte[edge * edge * 4];
            float last = edge - 1;

            for (int y = 0; y < edge; y++)
            {
                float v = y / last;
                for (int x = 0; x < edge; x++)
                {
                    float u = x / last;
                    Vector3 top = Vector3.Lerp(c1, c2, u);
                    Vector3 bottom = Vector3.Lerp(c3, c4, u);
                    Vector3 c = Vector3.Lerp(top, bottom, v);

                    int o = (y * edge + x) * 4;
                    pixels[o] = (byte)Math.Round(Math.Clamp(c.X, 0f, 1f) * 255f);
                    pixels[o + 1] = (byte)Math.Round(Math.Clamp(c.Y, 0f, 1f) * 255f);
                    pixels[o + 2] = (byte)Math.Round(Math.Clamp(c.Z, 0f, 1f) * 255f);
                    pixels[o + 3] = 255;
                }
            }

            return new ArtworkPixelData(pixels);
        }

        public override void Dispose()
        {
            lock (_gate)
            {
                _rotationTarget?.Dispose();
                _rotationTarget = null;
                _blurTarget?.Dispose();
                _blurTarget = null;
                _upscaledTarget?.Dispose();
                _upscaledTarget = null;
                _blurEffect?.Dispose();
                _blurEffect = null;
                _scaleEffect?.Dispose();
                _scaleEffect = null;

                _rotationEffect?.Dispose();
                _rotationEffect = null;
                _compositeEffect?.Dispose();
                _compositeEffect = null;

                _coverBitmaps[0]?.Dispose();
                _coverBitmaps[1]?.Dispose();
                _coverBitmaps[0] = null;
                _coverBitmaps[1] = null;
                _meshBitmap?.Dispose();
                _meshBitmap = null;
                _pendingArtwork = null;
                _realArtwork = null;
            }
        }
    }
}
