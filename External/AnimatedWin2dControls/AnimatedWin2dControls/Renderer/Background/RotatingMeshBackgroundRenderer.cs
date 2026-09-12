using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Shaders.Background;
using ComputeSharp.D2D1;
using ComputeSharp.D2D1.Interop;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Serilog;
using System;
using Windows.Graphics.DirectX;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AnimatedWin2dControls.Renderer.Background
{
    /// <summary>
    /// Rotating artwork and pinch mesh ported from Lyricify-Backgrounds (Apache 2.0).
    /// Rotation and blur run at 1/8 resolution. A spatially indexed triangle pass
    /// reproduces PinchVertex coverage at output resolution; the composite point-samples
    /// its UV field to preserve overdraw boundaries and applies theme treatment/dither.
    /// </summary>
    public sealed class RotatingMeshBackgroundRenderer : BaseBackgroundRenderer
    {
        /// <summary>Rotation/blur density, corresponding to the original backdrop downsample.</summary>
        private const float BackdropPixelScale = 1f / 8f;

        /// <summary>
        /// 中间层上的高斯 σ。原版 σ_uv = 170/输出宽，映射到 1/8 目标即 21.25px，
        /// 与分辨率无关（BlurAmount 以 96DPI 目标的 DIP 计，此处 DIP=px）。
        /// </summary>
        private const float BackdropBlurSigma = 21.25f;

        /// <summary>
        /// pinch 网格变形的半周期（秒）：phase = acos(sin(t·π/本值))/π，
        /// from↔to 全往复周期 = 10 秒，与原版一致。
        /// </summary>
        private const float MeshWarpTimeScale = 5f;
        // 主题适配强度：暗色按 luma ×(1-α) 压暗，亮色按 lerp(luma, 1, α) 提亮，
        // 色度向量均不参与混合（见 RotatingMeshCompositeEffect.ShiftLuma）。
        private const float DarkLumaStrength = 0.4f;
        private const float LightLumaStrength = 0.45f;
        private const float PortraitTextureScale = 1f;
        private const float LandscapeTextureScale = 0.8f;

        /// <summary>
        /// 旋转层整体速率倍数：保留原版 120/70/90 秒周期。
        /// </summary>
        private const float RotationScale = 1f;

        /// <summary>换歌封面交叉淡化时长（秒）。</summary>
        private const float ArtworkTransitionDuration = 0.8f;

        // 保护效果/中间目标生命周期：Dispose/LoadResources 持锁，Draw 走 TryEnter(0)
        // 抢不到就丢一帧，渲染线程永不阻塞（与 PS3XMB 渲染器一致）。
        private readonly object _gate = new();

        private PixelShaderEffect<RotatingMeshRotationEffect>? _rotationEffect;
        private PixelShaderEffect<RotatingMeshSolveEffect>? _solveEffect;
        private PixelShaderEffect<RotatingMeshCompositeEffect>? _compositeEffect;
        private GaussianBlurEffect? _blurEffect;
        private ScaleEffect? _scaleEffect;
        private CanvasRenderTarget? _rotationTarget;
        private CanvasRenderTarget? _solveTarget;
        private CanvasRenderTarget? _blurTarget;
        private CanvasRenderTarget? _upscaledTarget;
        private int _targetWidth;
        private int _targetHeight;
        private int _solveWidth;
        private int _solveHeight;
        private float _targetDpi;
        private float _targetWidthDip;
        private float _targetHeightDip;

        // 双槽封面位图：A/B 各一张（128×128，96 DPI），换歌时新封面写入非活动槽并
        // 交叉淡化。与网格一致走效果输入，可对旧图显式 Dispose（无终结器延迟）。
        private readonly CanvasBitmap?[] _coverBitmaps = new CanvasBitmap?[2];
        private int _activeArtworkSlot;
        private float _artworkTransitionStart;
        private bool _artworkTransitioning;
        private D2D1ResourceTextureManager? _meshVertices;
        private D2D1ResourceTextureManager? _meshCells;
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
                _solveTarget?.Dispose();
                _solveTarget = null;
                _blurTarget?.Dispose();
                _blurTarget = null;
                _upscaledTarget?.Dispose();
                _upscaledTarget = null;
                _blurEffect?.Dispose();
                _blurEffect = null;
                _scaleEffect?.Dispose();
                _scaleEffect = null;
                _meshVertices = null;
                _meshCells = null;
                _coverBitmaps[0]?.Dispose();
                _coverBitmaps[1]?.Dispose();
                _coverBitmaps[0] = null;
                _coverBitmaps[1] = null;

                // 效果绑定旧设备的已实现资源，设备重建后必须整体重建；
                // 位图输入为设备绑定资源，重建后由 Draw 惰性重创建并重新绑定。
                _rotationEffect?.Dispose();
                _rotationEffect = new PixelShaderEffect<RotatingMeshRotationEffect>();
                _solveEffect?.Dispose();
                _solveEffect = new PixelShaderEffect<RotatingMeshSolveEffect>();
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
                EnsureMeshResources(backdropHeight > backdropWidth);
                EnsureCoverBitmaps(control);
                PushPendingArtwork(control);

                // PinchVertex：phase = acos(sin(Time·π/MeshWarpTimeScale))/π，mix = smoothstep(phase)。
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

                // Pass 0 —— 按三角形覆盖顺序生成全分辨率 UV，折叠区域不做插值。
                if (_meshVertices is not null && _meshCells is not null && _solveEffect is not null && _solveTarget is not null)
                {
                    _solveEffect.ConstantBuffer = new RotatingMeshSolveEffect(
                        new float2(_solveWidth, _solveHeight),
                        pinchMix,
                        _meshRows,
                        _meshColumns, pinchTextureScale, pinchTextureOffset);

                    // EndDraw 边界①（solve 会话 Dispose）：失败自愈 + 记录，截停本帧，
                    // 阻止异常逃逸出 Draw 被游戏循环转成 stowed exception（0xc000027b）闪退。
                    try
                    {
                        using (var solveSession = _solveTarget!.CreateDrawingSession())
                        {
                            solveSession.DrawImage(_solveEffect);
                        }
                    }
                    catch (Exception ex) { LogPassFailure("pass0-solve", control, ds, ex); return; }
                }

                // Pass 1 —— 旋转封面层绘制到 1/8 中间目标。
                _rotationEffect!.ConstantBuffer = new RotatingMeshRotationEffect(
                    new float2(_targetWidth, _targetHeight),
                    time,
                    rotationScale: RotationScale,
                    imageScale: 1f,
                    artworkMix);

                // EndDraw 边界②（rotation 会话 Dispose）。
                try
                {
                    using (var rotationSession = _rotationTarget!.CreateDrawingSession())
                    {
                        rotationSession.DrawImage(_rotationEffect);
                    }
                }
                catch (Exception ex) { LogPassFailure("pass1-rotation", control, ds, ex); return; }

                // Pass 2 —— 原生高斯模糊（Soft 边框 ≡ 原版零边框 + 覆盖率归一化）。
                // EndDraw 边界③（blur 会话 Dispose）。
                try
                {
                    using (var blurSession = _blurTarget!.CreateDrawingSession())
                    {
                        // Soft blur contains partial alpha. Source-over on a reused
                        // target would accumulate older frames along the border.
                        blurSession.Clear(Microsoft.UI.Colors.Transparent);
                        blurSession.DrawImage(_blurEffect!);
                    }
                }
                catch (Exception ex) { LogPassFailure("pass2-blur", control, ds, ex); return; }

                // Pass 2.5 —— 上采样到全屏位图：合成 pass 的输入全部为普通位图，
                // 避免效果嵌效果（composite ← ScaleEffect ← ...）的图配置风险。
                // EndDraw 边界④（upscale 会话 Dispose）。
                try
                {
                    using (var upscaleSession = _upscaledTarget!.CreateDrawingSession())
                    {
                        upscaleSession.Clear(Microsoft.UI.Colors.Transparent);
                        upscaleSession.DrawImage(_scaleEffect!);
                    }
                }
                catch (Exception ex) { LogPassFailure("pass2.5-upscale", control, ds, ex); return; }

                // Pass 3 —— 材质处理 + pinch 网格 uv 重建 + 抖动，输出全屏。
                // 网格纹理或求解目标创建彻底失败时跳过合成，直接呈现模糊背景
                // （保持不透明覆盖）。
                if (_meshVertices is not null && _meshCells is not null && _solveTarget is not null)
                {
                    // EndDraw 边界⑤（bgCache 会话，含 Flush：上抛本会话内延迟累积的
                    // D2D1 错误，避免漂移到后续渲染器调用点干扰定位）。
                    try
                    {
                        _compositeEffect!.ConstantBuffer = new RotatingMeshCompositeEffect(
                            new float2(pixelWidth, pixelHeight),
                            IsDark,
                            IsDark ? DarkLumaStrength : LightLumaStrength,
                            ditherStrength: 1f);

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

                        ds.Flush();
                    }
                    catch (Exception ex) { LogPassFailure("pass3-composite", control, ds, ex); return; }
                }
                else
                {
                    try
                    {
                        ds.DrawImage(_upscaledTarget);
                    }
                    catch (Exception ex) { LogPassFailure("pass3-fallback-blit", control, ds, ex); return; }
                }
            }
            finally { Monitor.Exit(_gate); }
        }

        // ── 渲染失败自愈（正常渲染零开销：无异常时无分配、无 I/O）──────

        /// <summary>
        /// 单个 EndDraw 边界失败的处理：清空 DPI 缓存标记强制下一帧整体重建并重新
        /// 绑定（瞬态坏状态不跨帧滞留）。调用方随后截停本帧——异常绝不能逃逸出
        /// Draw，否则会被游戏循环转成 stowed exception（0xc000027b）直接闪退。
        /// 错误详情仅在 Debug 构建写入日志（Release 零日志开销）。
        /// </summary>
        private void LogPassFailure(string tag, ICanvasAnimatedControl control, CanvasDrawingSession ds, Exception ex)
        {
            _targetDpi = 0f;
            #if DEBUG
            Log.ForContext<RotatingMeshBackgroundRenderer>().Error(ex,
                "[render] {Tag} 失败，已截停本帧并标记整体重建。图状态：{State}", tag, DumpGraphState(control, ds));
            #endif
        }

        /// <summary>一帧内全部 D2D 资源的 DPI/尺寸/格式与效果图绑定关系快照。</summary>
        private string DumpGraphState(ICanvasAnimatedControl control, CanvasDrawingSession ds)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"  control: dpi={control.Dpi:F1} sizeDip={control.Size.Width:F0}x{control.Size.Height:F0} sessionDpi={ds.Dpi:F1}");
                sb.Append($"\n  fields: targetDpi={_targetDpi:F1} solve={_solveWidth}x{_solveHeight} time={Time:F2} transitioning={_artworkTransitioning} slot={_activeArtworkSlot} opacity={Opacity:F2}");
                sb.Append("\n  resources:");
                Describe(sb, " rotationT", _rotationTarget);
                Describe(sb, " blurT", _blurTarget);
                Describe(sb, " upscaledT", _upscaledTarget);
                Describe(sb, " solveT", _solveTarget);
                sb.Append($" meshResources={_meshVertices is not null && _meshCells is not null}");
                Describe(sb, " coverA", _coverBitmaps[0]);
                Describe(sb, " coverB", _coverBitmaps[1]);
                sb.Append("\n  bindings:");
                sb.Append($" composite[{SourcesOf(_compositeEffect, 0, 1)}]");
                sb.Append($" solveResources={_meshVertices is not null && _meshCells is not null}");
                sb.Append($" rotation[{SourcesOf(_rotationEffect, 0, 1)}]");
                sb.Append($" blur.src={Name(_blurEffect?.Source)} scale.src={Name(_scaleEffect?.Source)}");
                return sb.ToString();
            }
            catch (Exception x)
            {
                return "  state-dump failed: " + x.Message;
            }
        }

        private static void Describe(System.Text.StringBuilder sb, string label, CanvasBitmap? bitmap)
        {
            if (bitmap is null) { sb.Append(label).Append("=<null>"); return; }
            sb.Append(label).Append($"(dpi={bitmap.Dpi:F1} px={bitmap.SizeInPixels.Width}x{bitmap.SizeInPixels.Height} fmt={bitmap.Format})");
        }

        private static string SourcesOf(object? effect, params int[] indexes)
        {
            try
            {
                if (effect is null) return "<effect-null>";
                if (effect.GetType().GetProperty("Sources")?.GetValue(effect) is not System.Collections.IList list)
                    return "<no-sources>";
                var parts = new System.Collections.Generic.List<string>();
                foreach (int i in indexes)
                    parts.Add(i < list.Count ? Name(list[i]) : $"<missing:{i}>");
                return string.Join(",", parts);
            }
            catch (Exception x) { return "<err:" + x.Message + ">"; }
        }

        private static string Name(object? source)
        {
            if (source is null) return "<null>";
            if (source is CanvasBitmap bmp)
                return $"bmp(dpi={bmp.Dpi:F1} px={bmp.SizeInPixels.Width}x{bmp.SizeInPixels.Height})";
            return source.GetType().Name;
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
                && _targetHeight == backdropHeight && _targetDpi == dpi
                && _targetWidthDip == widthDip && _targetHeightDip == heightDip)
                return;

            // 注意：DPI 变化（窗口跨屏）也必须整体重建，否则中间目标与绘制会话的
            // DPI 不一致会触发 ComputeSharp 的 DPI 补偿节点，导致图配置错误。
            _rotationTarget?.Dispose();
            _rotationTarget = null;
            _blurTarget?.Dispose();
            _blurTarget = null;
            _upscaledTarget?.Dispose();
            _upscaledTarget = null;
            _solveTarget?.Dispose();
            _solveTarget = null;
            _blurEffect?.Dispose();
            _scaleEffect?.Dispose();

            // 显式 96 DPI：两参数构造会继承 control 的 DPI，破坏"DIP=px"假设。
            // rotation/blur 目标为 1/8 像素密度，DIP 尺寸 = 像素尺寸。
            //
            // 中间目标统一 FP16：模糊后的是极平滑低频渐变，相邻纹素真实差远小于
            // 1/255，8bit 存储会量化出 1-LSB 台阶，经上采样放大为宽色带——合成
            // pass 的最终抖动只作用于输出量化，救不回已烧进中间纹理的台阶，且
            // 饱和度补偿还会放大其对比。FP16 在 [0,1] 区间精度约 2^-11（< 0.125
            // LSB），整条中间链路无量化。极少数不支持 FP16 渲染目标的设备回退
            // 8bit 默认格式（表现与历史版本一致）。
            try
            {
                _rotationTarget = new CanvasRenderTarget(
                    control, backdropWidth, backdropHeight, 96f,
                    DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
                _blurTarget = new CanvasRenderTarget(
                    control, backdropWidth, backdropHeight, 96f,
                    DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);

                // 上采样目标与主画布同尺寸同 DPI（与 bgCache 一致），作为合成 pass 的输入 0。
                _upscaledTarget = new CanvasRenderTarget(
                    control, widthDip, heightDip, dpi,
                    DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
            }
            catch (Exception ex)
            {
                // FP16 渲染目标创建失败（格式不支持/设备异常）：降级 8bit 默认格式。
#if DEBUG
                Log.Error(ex, "FP16 中间目标创建失败，降级 8bit 默认格式");
#endif
                _rotationTarget?.Dispose();
                _blurTarget?.Dispose();
                _upscaledTarget?.Dispose();

                _rotationTarget = new CanvasRenderTarget(control, backdropWidth, backdropHeight, 96f);
                _blurTarget = new CanvasRenderTarget(control, backdropWidth, backdropHeight, 96f);
                _upscaledTarget = new CanvasRenderTarget(control, widthDip, heightDip);
            }

            _blurEffect = new GaussianBlurEffect
            {
                Source = _rotationTarget,
                BlurAmount = BackdropBlurSigma,
                Optimization = EffectOptimization.Balanced,
                BorderMode = EffectBorderMode.Soft,
            };

            _scaleEffect = new ScaleEffect
            {
                Source = _blurTarget,
                Scale = new Vector2(widthDip / backdropWidth, heightDip / backdropHeight),
                InterpolationMode = CanvasImageInterpolation.Linear,
            };

            // 求解目标：全像素密度的 RGBA32F（uv 场需要浮点精度）。必须以控制
            // DPI 创建（DIP 尺寸 = 像素数 × 96/dpi），与合成 pass 的绘制会话同
            // DPI——不一致会触发 ComputeSharp 的 DPI 补偿节点，导致图配置错误。
            // RGBA32F 不受支持等异常时留空，Draw 走模糊背景回落，渲染不中断。
            int solveWidth = control.ConvertDipsToPixels(widthDip, CanvasDpiRounding.Round);
            int solveHeight = control.ConvertDipsToPixels(heightDip, CanvasDpiRounding.Round);
            solveWidth = Math.Max(1, solveWidth);
            solveHeight = Math.Max(1, solveHeight);

            try
            {
                _solveTarget = new CanvasRenderTarget(
                    control,
                    solveWidth * 96f / dpi,
                    solveHeight * 96f / dpi,
                    dpi,
                    DirectXPixelFormat.R32G32B32A32Float,
                    CanvasAlphaMode.Premultiplied);
                _solveWidth = solveWidth;
                _solveHeight = solveHeight;
            }
            catch (Exception ex)
            {
                // 求解目标创建失败：置空并在日志留痕（合成图 Sources[1] 同帧无条件
                // 重绑为 null，合成 pass 跳过，Draw 走模糊背景回落）。
#if DEBUG
                Log.Error(ex, "求解目标（R32G32B32A32Float）创建失败，Draw 走模糊背景回落");
#endif
                _solveTarget = null;
            }

            _compositeEffect!.Sources[0] = _upscaledTarget;
            // 无条件重绑（含 null）：若 solve 目标某次创建失败，残留的旧目标输入本身就是
            // 无效图；null 时合成 pass 由 Draw 按条件跳过，不会消费到空输入。
            _compositeEffect.Sources[1] = _solveTarget;
            _targetWidth = backdropWidth;
            _targetHeight = backdropHeight;
            _targetDpi = dpi;
            _targetWidthDip = widthDip;
            _targetHeightDip = heightDip;
        }

        private void EnsureMeshResources(bool isPortrait)
        {
            if (_meshVertices is not null && _meshCells is not null && _meshIsPortrait == isPortrait)
                return;

            RotatingMeshWarp.MeshData mesh = RotatingMeshWarp.Create(
                isPortrait ? RotatingMeshWarp.ResolvePortraitPreset(_presetSlot) : _presetSlot,
                isPortrait);
            var packed = new float[mesh.Rows * mesh.Columns * 4];
            for (int i = 0; i < mesh.From.Length; i++)
            {
                packed[i * 4] = mesh.From[i].X;
                packed[i * 4 + 1] = mesh.From[i].Y;
                packed[i * 4 + 2] = mesh.To[i].X;
                packed[i * 4 + 3] = mesh.To[i].Y;
            }
            var index = RotatingMeshSpatialIndex.Create(mesh);
            _meshVertices = null;
            _meshCells = null;
            try
            {
                // Data textures have no image-input/DPI/alpha processing. The solve
                // effect has zero image inputs, so resource registers start at zero.
                var vertices = CreateMeshResource(packed, mesh.Columns, mesh.Rows);
                var cells = CreateMeshResource(index.Texels, index.Width,
                    RotatingMeshSpatialIndex.TileCount * RotatingMeshSpatialIndex.TileCount);
                _solveEffect!.ResourceTextureManagers[0] = vertices;
                _solveEffect.ResourceTextureManagers[1] = cells;
                _meshVertices = vertices;
                _meshCells = cells;
                _meshRows = mesh.Rows;
                _meshColumns = mesh.Columns;
                _meshIsPortrait = isPortrait;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Rotating mesh resource creation failed; using blurred artwork");
            }
        }

        private static D2D1ResourceTextureManager CreateMeshResource(float[] data, int width, int height)
        {
            return new D2D1ResourceTextureManager(
                new uint[] { (uint)width, (uint)height },
                D2D1BufferPrecision.Float32, D2D1ChannelDepth.Four,
                D2D1Filter.MinMagMipPoint,
                new[] { D2D1ExtendMode.Clamp, D2D1ExtendMode.Clamp },
                MemoryMarshal.AsBytes(data.AsSpan()), new uint[] { (uint)width * 16 });
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
            catch (Exception ex)
            {
                // 创建失败：保留旧槽内容与活动状态，不发散（本帧放弃本次更新）。
#if DEBUG
                Log.Error(ex, "封面位图创建失败，本帧放弃本次封面更新");
#endif
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
            _solveTarget?.Dispose();
            _solveTarget = null;
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
                _solveEffect?.Dispose();
                _solveEffect = null;
                _compositeEffect?.Dispose();
                _compositeEffect = null;

                _coverBitmaps[0]?.Dispose();
                _coverBitmaps[1]?.Dispose();
                _coverBitmaps[0] = null;
                _coverBitmaps[1] = null;
                _meshVertices = null;
                _meshCells = null;
                _pendingArtwork = null;
                _realArtwork = null;
            }
        }
    }
}


