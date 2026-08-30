using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
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
    /// Apple Music 风格背景渲染器。移植自 Lyricify-Backgrounds（Apache 2.0）经
    /// ComputeSharpDemo 转写的 compute 版本，适配本项目 D2D1 像素着色器管线：
    ///
    /// <list type="number">
    /// <item><see cref="AppleMusicRotationEffect"/> —— 三层旋转封面 + aspect-fill
    /// 兜底，封面经 <c>D2D1ResourceTextureManager</c>（RGBA8，线性 + Clamp）注入，
    /// 绘制到 1/8 像素密度的中间目标（对应原版 1/7.53 背景面）。</item>
    /// <item>原生 <see cref="GaussianBlurEffect"/>（Soft 边框）做 77 抽头 σ 可分离
    /// 高斯模糊的等价实现：premultiplied 软边框模糊 + 合成 pass 内 un-premultiply，
    /// 与原版"零边框采样 + 覆盖率归一化"逐像素一致。</item>
    /// <item><see cref="AppleMusicCompositeEffect"/> —— 材质处理 + pinch 网格逆向
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
    public sealed class AppleMusicBackgroundRenderer : BaseBackgroundRenderer
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

        // 保护效果/中间目标生命周期：Dispose/LoadResources 持锁，Draw 走 TryEnter(0)
        // 抢不到就丢一帧，渲染线程永不阻塞（与 PS3XMB 渲染器一致）。
        private readonly object _gate = new();

        private PixelShaderEffect<AppleMusicRotationEffect>? _rotationEffect;
        private PixelShaderEffect<AppleMusicCompositeEffect>? _compositeEffect;
        private GaussianBlurEffect? _blurEffect;
        private ScaleEffect? _scaleEffect;
        private CanvasRenderTarget? _rotationTarget;
        private CanvasRenderTarget? _blurTarget;
        private CanvasRenderTarget? _upscaledTarget;
        private int _targetWidth;
        private int _targetHeight;
        private float _targetDpi;
        private float _meshDpi;

        private readonly D2D1ResourceTextureManager?[] _artworkManagers = new D2D1ResourceTextureManager?[2];
        private int _activeArtworkSlot;
        private float _artworkTransitionStart;
        private bool _artworkTransitioning;
        private CanvasBitmap? _meshBitmap;
        private bool _meshIsPortrait;
        private int _meshRows;
        private int _meshColumns;

        private readonly int _presetSlot = AppleMusicMesh.SelectPresetSlot();

        // 渲染线程每帧在锁内取走并推入资源纹理；引用赋值原子，无需额外同步。
        private ArtworkPixelData? _pendingArtwork;
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

                // 效果绑定旧设备的已实现资源，设备重建后必须整体重建。
                _rotationEffect?.Dispose();
                _rotationEffect = new PixelShaderEffect<AppleMusicRotationEffect>();
                _compositeEffect?.Dispose();
                _compositeEffect = new PixelShaderEffect<AppleMusicCompositeEffect>();

                // 资源纹理管理器与设备无关，可在多个效果实例间共享复用。
                for (int i = 0; i < 2; i++)
                {
                    if (_artworkManagers[i] is not null)
                        _rotationEffect.ResourceTextureManagers[i] = _artworkManagers[i];
                }
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
                EnsureArtworkManagers();
                PushPendingArtwork();

                // PinchVertex：phase = acos(sin(Time * pi / 5)) / pi，mix = smoothstep(phase)。
                float time = Time;
                float phase = MathF.Acos(MathF.Sin(time * MathF.PI / MeshWarpTimeScale)) / MathF.PI;
                float pinchMix = phase * phase * (3f - 2f * phase);

                bool isPortrait = backdropHeight > backdropWidth;
                float pinchTextureScale = isPortrait ? PortraitTextureScale : LandscapeTextureScale;
                float pinchTextureOffset = (1f - pinchTextureScale) * 0.5f;

                // 换歌交叉淡化进度：1.2s smoothstep 推向当前封面槽位。
                float artworkMix;
                if (_artworkTransitioning)
                {
                    float t = Math.Clamp((time - _artworkTransitionStart) / 1.2f, 0f, 1f);
                    artworkMix = t * t * (3f - 2f * t);
                    if (_activeArtworkSlot == 0) artworkMix = 1f - artworkMix;
                    if (t >= 1f) _artworkTransitioning = false;
                }
                else
                {
                    artworkMix = _activeArtworkSlot == 1 ? 1f : 0f;
                }

                // Pass 1 —— 旋转封面层绘制到 1/8 中间目标。
                _rotationEffect!.ConstantBuffer = new AppleMusicRotationEffect(
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
                    _compositeEffect!.ConstantBuffer = new AppleMusicCompositeEffect(
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

            AppleMusicMesh.MeshData mesh = AppleMusicMesh.Create(
                isPortrait ? AppleMusicMesh.ResolvePortraitPreset(_presetSlot) : _presetSlot,
                isPortrait);

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

        private void EnsureArtworkManagers()
        {
            if (_artworkManagers[0] is not null)
                return;

            // 双槽固定 Edge² 尺寸：换歌写入非活动槽并交叉淡化，仅需 Update 不重建。
            for (int i = 0; i < 2; i++)
            {
                _artworkManagers[i] = new D2D1ResourceTextureManager(
                    stackalloc uint[] { ArtworkPixelData.Edge, ArtworkPixelData.Edge },
                    D2D1BufferPrecision.UInt8Normalized,
                    D2D1ChannelDepth.Four,
                    D2D1Filter.MinMagMipLinear,
                    stackalloc D2D1ExtendMode[] { D2D1ExtendMode.Clamp, D2D1ExtendMode.Clamp },
                    CreateDefaultArtwork().Pixels,
                    stackalloc uint[] { ArtworkPixelData.Edge * 4 });

                _rotationEffect!.ResourceTextureManagers[i] = _artworkManagers[i];
            }

            _activeArtworkSlot = 0;
            _artworkTransitioning = false;
        }

        private void PushPendingArtwork()
        {
            var pending = _pendingArtwork;
            if (pending is null) return;
            _pendingArtwork = null;

            // 写入非活动槽并切槽，启动 1.2s 交叉淡化（与其它 shader 的像素平滑过渡对齐）。
            int slot = 1 - _activeArtworkSlot;

            _artworkManagers[slot]!.Update(
                stackalloc uint[] { 0, 0 },
                stackalloc uint[] { ArtworkPixelData.Edge, ArtworkPixelData.Edge },
                stackalloc uint[] { ArtworkPixelData.Edge * 4 },
                pending.Pixels);

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
            // 深蓝灰对角渐变，接近 Apple Music 无封面时的氛围色。
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

                // D2D1ResourceTextureManager 无 Dispose，交给终结器回收。
                for (int i = 0; i < 2; i++)
                {
                    _artworkManagers[i] = null;
                }

                _meshBitmap?.Dispose();
                _meshBitmap = null;
                _pendingArtwork = null;
                _realArtwork = null;
            }
        }
    }
}
