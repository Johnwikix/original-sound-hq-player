using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// Win2D 逐字歌词渲染器（薄宿主）：只渲染当前一行 + 翻译，单行块垂直居中，逐字扫光 +
    /// 发光/字浮/字缩动效（经 LyricsAnimator 驱动，触发语义与主界面一致）。
    /// 直接组装 External 库内部件（RenderLyricsLine / LyricsLineRenderer / LyricsLayoutManager /
    /// LyricsAnimator，均零静态总线依赖、全参数注入），绕开 LyricsRenderCoordinator 的整页滚动逻辑；
    /// 样式完全来自 <see cref="DesktopLyricsStyle"/>（不消费主界面 LyricsSettingsBus），
    /// 数据经 <see cref="IDesktopLyricsRenderer"/> 的 Set* 由宿主窗口推送（总线转发零改动）。
    /// 描边实现：EnsureCaches 的 CachedStroke 是白描边命令列表。有真实音节的行走逐字路径，
    /// 描边切片经 LyricsLineRenderer.IsStrokeEnabled 使用与填充相同的 dest 映射随字符一起变换
    /// （字浮/字缩时描边贴合字符，固定位置整行描边会错位）；LRC 整行路径无逐字变换，
    /// 由本宿主整行垫底。两者都以黑色 <see cref="RenderLyricsLine.UnplayedStrokeTint"/> 着色获得黑边。
    /// 线程模型：CanvasAnimatedControl 的 Update/Draw 在渲染线程回调，Set* 在 UI 线程调用；
    /// 与 LyricsRenderCoordinator 相同，仅依赖字段原子赋值传递状态，不持锁
    /// （Set* 只写字段/标志，Win2D 资源的全部创建与释放都在 Update/Draw 回调内）。
    /// </summary>
    public sealed class CanvasLyricsRenderer : IDesktopLyricsRenderer
    {
        private const double SyncThresholdMs = 500;   // 内部时钟与外部观测的硬同步阈值（seek/大跳才触发）
        private const double UnplayedOpacity = 0.4;   // 未播放部分与原色的明暗拉离比例：预混为不透明色。不能用半透明填充——
                                                      // 半透明色叠在黑描边内半环上会在字形内缘形成多层色阶。
                                                      // 压暗/提亮方向按歌词色亮度定，见 ComputeUnplayedFill
        private const double SecondaryOpacity = 0.6;  // 翻译行透明度（与文本渲染器 TransOpacity 一致）
        private const int TargetFrameRate = 60;
        private const string DefaultFontFamily = "Segoe UI";

        // 逐字动效量值（是否启用/强度/长音节阈值均由样式控制）：默认取主界面默认
        // （GlowAmount=5px / CharFloatAmount=5px / CharScaleAmount=110%→1.1）
        private const double FloatDurationMs = 450;

        private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent };
        private readonly LyricsLineRenderer _lineRenderer = new();
        private readonly LyricsAnimator _animator = new();
        private readonly List<RenderLyricsLine> _renderLineList = [];   // 单元素列表，供动画器驱动当前行
        private readonly ValueTransition<double> _scrollTransitionStub =
            new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);   // 动画器读取时长/插值器用，本宿主无滚动
        private int _animationVersion;
        private bool _lineJustRebuilt;

        private List<LyricLine>? _lyrics;
        private RenderLyricsLine? _currentLine;   // 仅当前行的 Win2D 资源；切行/样式/尺寸变化时整体重建
        private int _currentIndex = -1;
        private bool _lyricsChanged;
        private bool _layoutDirty = true;
        private double _lastLayoutWidth;
        private double _lastLayoutHeight;

        private double _internalTimeMs;
        private bool _timeSyncValid;
        private long _lastTotalMs;
        private double _offsetMs;
        private bool _isPlaying;

        private double _fontSize = 36;
        private string _fontFamily = DefaultFontFamily;
        private Color _color = Colors.White;
        private bool _outline = true;
        private double _outlineWidth = 1.5;
        private int _fontWeight = 400;
        private bool _showTranslation = true;
        private bool _glow = true;
        private bool _charFloat = true;
        private bool _charScale = true;
        private double _longSyllableThreshold = 700;
        private double _glowAmountPx = 5.0;
        private double _floatAmountPx = 5.0;
        private double _scaleFactor = 1.10;
        private bool _disposed;

        public CanvasLyricsRenderer()
        {
            _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / TargetFrameRate);
            _canvas.CreateResources += OnCanvasCreateResources;
            _canvas.Update += OnCanvasUpdate;
            _canvas.Draw += OnCanvasDraw;
        }

        public UIElement Content => _canvas;

        public void SetStyle(DesktopLyricsStyle style)
        {
            _fontSize = Math.Clamp(style.FontSize, 8, 300);
            _fontFamily = string.IsNullOrEmpty(style.FontFamily) ? DefaultFontFamily : style.FontFamily;
            _color = style.Color;
            _outline = style.Outline;
            _outlineWidth = Math.Clamp(style.OutlineWidth, 0, 20);
            _fontWeight = Math.Clamp(style.FontWeight, 100, 900);
            _showTranslation = style.ShowTranslation;
            _glow = style.Glow;
            _charFloat = style.CharFloat;
            _charScale = style.CharScale;
            _longSyllableThreshold = Math.Clamp(style.LongSyllableThreshold, 0, 5000);
            _glowAmountPx = Math.Clamp(style.GlowAmount, 0, 10);
            _floatAmountPx = Math.Clamp(style.CharFloatAmount, 0, 10);
            _scaleFactor = Math.Clamp(style.CharScaleAmount, 50, 150) / 100.0;
            // 字号/字体/字重/描边/翻译都影响布局，统一标记下帧重建（颜色/动效开关不触发，逐帧生效）
            _layoutDirty = true;
        }

        public void SetLyrics(IList<LyricLine>? lyrics)
        {
            _lyrics = lyrics as List<LyricLine> ?? (lyrics is null ? null : [.. lyrics]);
            _lyricsChanged = true;
        }

        public void SetPlaybackTime(long totalMs) => _lastTotalMs = totalMs;

        public void SetOffset(double offsetMs) => _offsetMs = offsetMs;

        public void SetIsPlaying(bool isPlaying)
        {
            if (_isPlaying == isPlaying) return;
            _isPlaying = isPlaying;
            if (isPlaying) _timeSyncValid = false;   // 恢复播放：下帧硬同步内部时钟（coordinator ResetTimeSync 同款）
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // 先停循环、摘事件，再丢弃引用。注意：绝不能在 UI 线程释放行的 Win2D 资源——
            // Paused=true 只阻止后续 tick，渲染线程可能正处于一次 Draw 的中段（关闭窗口与
            // 渲染 tick 并发），此时释放 CachedFill/效果链/文本布局会被 DrawSecondaryText 等
            // 访问到已释放的原生资源，抛出的异常在渲染线程上无法被 UI 线程的
            // UnhandledException(Handled=true) 吞掉，直接终止进程。
            // 资源交给 GC 与 Win2D 设备回收：窗口关闭后整窗资源一并消亡，无泄漏窗口期。
            _canvas.Paused = true;
            _canvas.CreateResources -= OnCanvasCreateResources;
            _canvas.Update -= OnCanvasUpdate;
            _canvas.Draw -= OnCanvasDraw;
            _currentLine = null;
            _renderLineList.Clear();
        }

        // ── Canvas 回调（渲染线程） ──────────────────────────────────────────

        private void OnCanvasCreateResources(CanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            // 设备重建后全部 Win2D 资源失效：弃旧行，下帧整体重建（coordinator OnCreateResources 同款思路）
            DisposeCurrentLine();
            _currentIndex = -1;
            _layoutDirty = true;
        }

        private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            if (_disposed) return;
            try
            {
                if (_lyricsChanged)
                {
                    DisposeCurrentLine();
                    _currentIndex = -1;
                    _lyricsChanged = false;
                    _layoutDirty = true;
                }

                // 时间平滑：播放中内部时钟按帧自增（60fps 平滑），仅当外部观测与内部时钟的
                // 偏差超阈值（seek/大跳）才硬同步。注意不能用"相邻帧外部增量"作判据
                // （coordinator 同款写法）：桌面歌词消费的进度快照是底层 250ms 轮询的阶梯值，
                // 相邻帧增量恰好 ≈250ms，会频繁误触发硬同步把扫光钳成阶梯跳变。暂停时直接跟随外部时间。
                double externalTimeMs = _lastTotalMs;
                if (_isPlaying)
                {
                    if (!_timeSyncValid || Math.Abs(externalTimeMs - _internalTimeMs) > SyncThresholdMs)
                        _internalTimeMs = externalTimeMs;
                    else
                        _internalTimeMs += args.Timing.ElapsedTime.TotalMilliseconds;
                    _timeSyncValid = true;
                }
                else
                {
                    _internalTimeMs = externalTimeMs;
                    _timeSyncValid = true;
                }
                double currentTimeMs = _internalTimeMs - _offsetMs;

                int newIndex = FindCurrentLineIndex(currentTimeMs);
                bool lineChanged = newIndex != _currentIndex;
                _currentIndex = newIndex;

                double width = _canvas.Size.Width;
                double height = _canvas.Size.Height;
                bool sizeChanged = Math.Abs(width - _lastLayoutWidth) > 0.5 || Math.Abs(height - _lastLayoutHeight) > 0.5;
                if (_layoutDirty || lineChanged || sizeChanged)
                    RebuildLine(sender, newIndex, width, height);

                // 逐字动效（发光/字浮/字缩）、逐字符"正在播放"标记与透明度过渡统一交给
                // LyricsAnimator 驱动（触发语义与主界面一致）：单元素列表 = 当前播放行。
                // 字符标记缺失会让 ComputeRegionPlayedWidth 丢失字符内分数进度（扫光按整字符跳变）。
                if (_renderLineList.Count > 0)
                {
                    _animationVersion++;
                    _animator.UpdateLines(
                        _renderLineList,
                        startIndex: 0,
                        endIndex: 0,
                        primaryPlayingLineIndex: 0,
                        lyricsWidth: width,
                        lyricsHeight: height,
                        targetYScrollOffset: 0,
                        playingLineTopOffsetFactor: 0.5,
                        isLyricsBlurEffectEnabled: false,
                        isLyricsOutOfSightEffectEnabled: false,
                        isLyricsFadeOutEffectEnabled: false,
                        unplayedPrimaryOpacity: 1.0,   // 未播放填充不透明（压暗预混进颜色，见 UnplayedOpacity 注释）
                        playedPrimaryOpacity: 1.0,
                        secondaryOpacity: SecondaryOpacity,
                        isLyricsGlowEffectEnabled: _glow,
                        lyricsGlowEffectAmount: _glowAmountPx,
                        lyricsGlowEffectLongSyllableDuration: _longSyllableThreshold,
                        isLyricsFloatAnimationEnabled: _charFloat,
                        lyricsFloatAnimationAmount: _floatAmountPx,
                        lyricsFloatAnimationDuration: FloatDurationMs,
                        isLyricsScaleEffectEnabled: _charScale,
                        lyricsScaleEffectAmount: _scaleFactor,
                        lyricsScaleEffectLongSyllableDuration: _longSyllableThreshold,
                        blurAmountMax: 0,
                        canvasYScrollTransition: _scrollTransitionStub,
                        elapsedTime: args.Timing.ElapsedTime,
                        isMouseScrolling: false,
                        isMouseScrollingChanged: false,
                        isLayoutChanged: _lineJustRebuilt,
                        isPrimaryPlayingLineChanged: lineChanged,
                        currentPositionMs: currentTimeMs,
                        animationVersion: _animationVersion);
                }
                _lineJustRebuilt = false;
            }
            catch (Exception)
            {
                // 渲染线程异常绝不允许逃逸（逃逸 = 进程终止，见 Dispose 注释）；标记重建，下一帧重试
                _layoutDirty = true;
            }
        }

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            if (_disposed) return;
            var ds = args.DrawingSession;
            ds.Clear(Colors.Transparent);

            var line = _currentLine;
            if (line?.PrimaryTextLayout is null) return;
            if (line.PrimaryTextLayout.LayoutBounds.Width <= 0) return;

            int strokeWidth = (int)(_outline ? _outlineWidth : 0);
            double currentTimeMs = _internalTimeMs - _offsetMs;

            try
            {
                // 缓存（含 UnplayedComposite）在 MeasureAndArrange 时被释放，惰性重建必须先于判空
                line.EnsureCaches(sender, strokeWidth);
                if (line.CachedFill is null || line.UnplayedComposite is null) return;

                if (line.UnplayedFillTint != null)
                    line.UnplayedFillTint.Color = _color;          // 翻译行/暂停态的填充色
                if (line.UnplayedStrokeTint != null)
                    line.UnplayedStrokeTint.Color = Colors.Black;  // 黑描边

                // 未播放填充 = 与歌词色拉开明暗的不透明预混色（亮色向黑压暗/暗色向白提亮）：
                // 完整盖住描边的内半环，避免半透明填充与描边叠出多层色阶；
                // 已播放填充保持原色不透明。固定向黑压暗在黑字模式下会与已播放同色，扫光消失
                var unplayedFill = ComputeUnplayedFill();

                // 单行块垂直居中：MeasureAndArrange 自上而下定位，BottomRightPosition.Y 即块高
                float offsetY = (float)Math.Max(0, (_canvas.Size.Height - line.BottomRightPosition.Y) / 2);
                var prevTransform = ds.Transform;
                ds.Transform *= Matrix3x2.CreateTranslation(0, offsetY);

                _lineRenderer.IsPlaying = line.GetIsPlaying(currentTimeMs);
                _lineRenderer.CurrentProgressMs = currentTimeMs;
                _lineRenderer.Line = line;
                _lineRenderer.PlayedFillColor = _color;
                _lineRenderer.UnplayedFillColor = unplayedFill;
                _lineRenderer.IsGlowEnabled = _glow;
                _lineRenderer.IsScaleEnabled = _charScale;
                _lineRenderer.IsFloatEnabled = _charFloat;
                _lineRenderer.IsStrokeEnabled = strokeWidth > 0;   // 逐字路径的描边切片随字符变换

                // LRC 整行路径无逐字变换，描边用固定位置整行垫底；
                // 有真实音节的行走逐字路径，描边切片在 LyricsLineRenderer 内随字符变换绘制，
                // 垫底会与字浮/字缩后的填充错位，故此处跳过
                if (strokeWidth > 0 && !line.IsPrimaryHasRealSyllableInfo && line.UnplayedStrokeTint != null)
                    ds.DrawImage(line.UnplayedStrokeTint);

                _lineRenderer.Draw(sender, ds);
                ds.Transform = prevTransform;
            }
            catch (ObjectDisposedException)
            {
                // 设备丢失（coordinator 同款兜底）：弃缓存，下帧经 _layoutDirty 重建
                line.DisposeCaches();
                _layoutDirty = true;
            }
            catch (Exception)
            {
                // 渲染线程异常绝不允许逃逸（逃逸 = 进程终止，见 Dispose 注释）；标记重建，下一帧重试
                _layoutDirty = true;
            }
        }

        // ── 行构建与释放 ─────────────────────────────────────────────────────

        /// <summary>未播放填充色：按歌词色亮度（YIQ）选明暗拉离方向——亮色文字向黑压暗、
        /// 暗色文字（自适应黑字/用户深色）向白提亮，比例 <see cref="UnplayedOpacity"/>，
        /// 保证任何歌词色下已播放与未播放部分都有可见的扫光明暗差。</summary>
        private Color ComputeUnplayedFill()
        {
            double yiq = ((_color.R * 299) + (_color.G * 587) + (_color.B * 114)) / 1000.0;
            byte target = yiq >= 128 ? (byte)0 : byte.MaxValue;
            byte Mix(byte c) => (byte)Math.Round(c + (target - c) * UnplayedOpacity);
            return Color.FromArgb(255, Mix(_color.R), Mix(_color.G), Mix(_color.B));
        }

        /// <summary>重建当前行的数据与 Win2D 布局资源（切行/样式/尺寸/设备重建时调用）。</summary>
        private void RebuildLine(ICanvasResourceCreator resourceCreator, int index, double width, double height)
        {
            DisposeCurrentLine();
            _lastLayoutWidth = width;
            _lastLayoutHeight = height;
            // 画布尚未完成首次布局（Size 仍为 0）时不建布局，保持 _layoutDirty 等有效尺寸后重建，
            // 避免 CanvasTextLayout 收到负的可用宽度（MeasureAndArrange 内部要扣 40px 左右留白）
            if (width < 60 || height < 20)
            {
                _layoutDirty = true;
                return;
            }
            _layoutDirty = false;   // 尺寸有效，本次布局已消费脏标记
            if (index < 0 || _lyrics is null || index >= _lyrics.Count) return;

            try
            {
                var line = new RenderLyricsLine();
                line.LoadFromLyricLine(_lyrics[index], _lyrics[index].EndMs);
                LyricsLayoutManager.MeasureAndArrange(
                    resourceCreator,
                    [line],
                    (int)_fontSize,
                    _showTranslation ? (int)(_fontSize * 0.7) : 0,   // 0 = 不建翻译布局（ShowTranslation 关）
                    _fontFamily,
                    CanvasHorizontalAlignment.Center,
                    width,
                    height,
                    (int)(_outline ? _outlineWidth : 0),
                    _fontWeight);
                line.PlayedPrimaryOpacityTransition.Start(1.0);
                line.UnplayedPrimaryOpacityTransition.Start(1.0);
                line.SecondaryOpacityTransition.Start(SecondaryOpacity);
                _currentLine = line;
                _renderLineList.Add(line);
                _lineJustRebuilt = true;
            }
            catch (ObjectDisposedException)
            {
                DisposeCurrentLine();
                _layoutDirty = true;
            }
        }

        private void DisposeCurrentLine()
        {
            if (_currentLine is null) return;
            _currentLine.DisposeCaches();
            _currentLine.DisposeTextLayout();
            _currentLine.DisposeTextGeometry();
            _currentLine = null;
            _renderLineList.Clear();
        }

        /// <summary>二分查找当前行（StartMs &lt;= t 的最后一行；间隙期保持上一行，与文本渲染器行为一致）。</summary>
        private int FindCurrentLineIndex(double effectiveMs)
        {
            var lyrics = _lyrics;
            if (lyrics is null || lyrics.Count == 0) return -1;

            int lo = 0, hi = lyrics.Count - 1;
            int matched = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (lyrics[mid].StartMs <= effectiveMs)
                {
                    matched = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return matched;
        }
    }
}
