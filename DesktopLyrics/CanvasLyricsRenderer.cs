using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
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
    /// 无描边（共享库描边入口恒传 0）；可读性三重保障：环境自适应取色（按背景切黑/白文字色）+
    /// 文字色反相的软阴影（本类自建 CanvasCommandList 剪影 + GaussianBlurEffect，零共享库改动）。
    /// 线程模型：CanvasAnimatedControl 的 Update/Draw 在渲染线程回调，Set* 在 UI 线程调用；
    /// 与 LyricsRenderCoordinator 相同，仅依赖字段原子赋值传递状态，不持锁
    /// （Set* 只写字段/标志，Win2D 资源的全部创建与释放都在 Update/Draw 回调内）。
    /// </summary>
    public sealed class CanvasLyricsRenderer : IDesktopLyricsRenderer
    {
        private const double SyncThresholdMs = 500;   // 内部时钟与外部观测的硬同步阈值（seek/大跳才触发）
        private const double UnplayedOpacity = 0.4;   // 未播放部分与原色的明暗拉离比例：预混为不透明色，
                                                      // 避免半透明填充在发光/暂停态效果链里叠出多层色阶。
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
        private int _fontWeight = 400;
        private bool _showTranslation = true;
        private bool _glow = true;
        private bool _charFloat = true;
        private bool _charScale = true;
        private double _longSyllableThreshold = 700;
        private double _glowAmountPx = 5.0;
        private double _floatAmountPx = 5.0;
        private double _scaleFactor = 1.10;
        // 阴影强度（0–2，= 滑块百分比 / 50）：≤1 单层绘制（alpha = 强度），
        // >1 叠加第二遍（alpha = 强度-1）——整体不透明度上限 1，超过部分靠二次叠加加深光晕
        private double _shadowStrength = 1.0;
        private bool _disposed;

        // 反相软阴影（渲染线程持有，随行重建创建/释放，见 RebuildLine/DisposeCurrentLine）。
        // 主文本按字符垂直分条记录剪影（CharShadowSlice），绘制时以与共享库填充完全相同的
        // dest 映射逐字符绘制——阴影跟随字浮/字缩动效；无字符信息时退回整行剪影；
        // 翻译行无逐字动效，恒为整行剪影。画笔共享一个，每帧改色（阴影色 = 文字色反相），
        // 换色无需重建。命令列表惰性求值依赖 TextLayout 存活，二者同生命周期
        private CanvasSolidColorBrush? _shadowBrush;
        private List<CharShadowSlice>? _charSlices;
        private CanvasCommandList? _primaryWholeShadowList;
        private GaussianBlurEffect? _primaryWholeShadowBlur;
        private CanvasCommandList? _secondaryShadowList;
        private GaussianBlurEffect? _secondaryShadowBlur;

        private sealed record CharShadowSlice(RenderLyricsChar Char, CanvasCommandList List, GaussianBlurEffect Blur);

        /// <summary>跨线程文本边界盒：RebuildLine 在渲染线程写入、UI 线程读取。
        /// 用不可变引用的原子赋值传递（Nullable&lt;Rect&gt; 结构体直接跨线程读会有撕裂风险）。</summary>
        private sealed class TextBoundsBox
        {
            public TextBoundsBox(Rect value) => Value = value;
            public Rect Value { get; }
        }

        private volatile TextBoundsBox? _lastTextBounds;

        /// <summary>最近一次实际绘制的文本区域（元素坐标 DIP，主文本+翻译合并边界）；null = 当前无文本。</summary>
        public Rect? LastTextBounds => _lastTextBounds?.Value;

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
            _fontWeight = Math.Clamp(style.FontWeight, 100, 900);
            _showTranslation = style.ShowTranslation;
            _glow = style.Glow;
            _charFloat = style.CharFloat;
            _charScale = style.CharScale;
            _longSyllableThreshold = Math.Clamp(style.LongSyllableThreshold, 0, 5000);
            _glowAmountPx = Math.Clamp(style.GlowAmount, 0, 10);
            _floatAmountPx = Math.Clamp(style.CharFloatAmount, 0, 10);
            _scaleFactor = Math.Clamp(style.CharScaleAmount, 50, 150) / 100.0;
            bool shadowWasEnabled = _shadowStrength > 0;
            _shadowStrength = Math.Clamp(style.ShadowAmount, 0, 100) / 50.0;
            // 阴影开↔关切换需重建/丢弃剪影资源；仅强度变化不改资源，逐帧生效（同颜色）
            if ((_shadowStrength > 0) != shadowWasEnabled)
                _layoutDirty = true;
            // 字号/字体/字重/翻译都影响布局，统一标记下帧重建（颜色/动效开关不触发，逐帧生效）
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

            double currentTimeMs = _internalTimeMs - _offsetMs;

            try
            {
                // 缓存（含 UnplayedComposite）在 MeasureAndArrange 时被释放，惰性重建必须先于判空；
                // 无描边，共享库的描边宽度入口恒传 0（CachedStroke 保持空命令列表，不产生可见描边）
                line.EnsureCaches(sender, 0);
                if (line.CachedFill is null || line.UnplayedComposite is null) return;

                if (line.UnplayedFillTint != null)
                    line.UnplayedFillTint.Color = _color;   // 翻译行/暂停态的填充色

                // 未播放填充 = 与歌词色拉开明暗的不透明预混色（亮色向黑压暗/暗色向白提亮）：
                // 已播放填充保持原色不透明。固定向黑压暗在黑字模式下会与已播放同色，扫光消失
                var unplayedFill = ComputeUnplayedFill();

                // 单行块垂直居中：MeasureAndArrange 自上而下定位，BottomRightPosition.Y 即块高
                float offsetY = (float)Math.Max(0, (_canvas.Size.Height - line.BottomRightPosition.Y) / 2);
                var prevTransform = ds.Transform;
                ds.Transform *= Matrix3x2.CreateTranslation(0, offsetY);

                // 反相软阴影垫底（与文字同一坐标系），其后共享库绘制填充文字
                DrawTextShadow(ds);

                _lineRenderer.IsPlaying = line.GetIsPlaying(currentTimeMs);
                _lineRenderer.CurrentProgressMs = currentTimeMs;
                _lineRenderer.Line = line;
                _lineRenderer.PlayedFillColor = _color;
                _lineRenderer.UnplayedFillColor = unplayedFill;
                _lineRenderer.IsGlowEnabled = _glow;
                _lineRenderer.IsScaleEnabled = _charScale;
                _lineRenderer.IsFloatEnabled = _charFloat;

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
                    0,   // 描边宽度恒 0（无描边）
                    _fontWeight);
                ReportTextBounds(line, height);
                line.PlayedPrimaryOpacityTransition.Start(1.0);
                line.UnplayedPrimaryOpacityTransition.Start(1.0);
                line.SecondaryOpacityTransition.Start(SecondaryOpacity);
                _currentLine = line;
                RebuildShadowResources(resourceCreator);
                _renderLineList.Add(line);
                _lineJustRebuilt = true;
            }
            catch (ObjectDisposedException)
            {
                DisposeCurrentLine();
                _layoutDirty = true;
            }
        }

        /// <summary>上报当前行实际绘制边界（元素坐标 DIP，主文本+翻译合并）：
        /// 布局边界 + 布局内位置偏移，再补上垂直居中平移——OnCanvasDraw 绘制时才加
        /// offsetY（(画布高-块高)/2，见 OnCanvasDraw），此处用同样的公式补偿，
        /// 窗口据此把环境取色采样收窄到歌词实际所在的背景环带。</summary>
        private void ReportTextBounds(RenderLyricsLine line, double canvasHeight)
        {
            double offsetY = Math.Max(0, (canvasHeight - line.BottomRightPosition.Y) / 2);
            bool hasBounds = false;
            Rect bounds = default;
            if (line.PrimaryTextLayout is { } primary)
            {
                var b = primary.LayoutBounds;
                bounds = new Rect(b.X + line.PrimaryPosition.X, b.Y + line.PrimaryPosition.Y + offsetY, b.Width, b.Height);
                hasBounds = true;
            }
            if (line.SecondaryTextLayout is { } secondary)
            {
                var b = secondary.LayoutBounds;
                var translation = new Rect(b.X + line.SecondaryPosition.X, b.Y + line.SecondaryPosition.Y + offsetY, b.Width, b.Height);
                if (hasBounds) bounds.Union(translation);
                else bounds = translation;
                hasBounds = true;
            }
            _lastTextBounds = hasBounds ? new TextBoundsBox(bounds) : null;
        }

        private void DisposeCurrentLine()
        {
            DisposeShadowResources();   // 阴影资源与行同生命周期，先于行成员释放（含 _currentLine 为空的早退路径）
            if (_currentLine is null) return;
            _currentLine.DisposeCaches();
            _currentLine.DisposeTextLayout();
            _currentLine.DisposeTextGeometry();
            _currentLine = null;
            _renderLineList.Clear();
            _lastTextBounds = null;   // 行已销毁（切行/无歌词/设备重建），窗口回退窗口环带采样
        }

        // ── 反相软阴影 ───────────────────────────────────────────────────────

        /// <summary>按当前行字形重建阴影剪影（渲染线程，仅行构建时调用）。主文本按字符
        /// 垂直分条（在字符布局矩形边界处裁开，相邻字符的光晕互不叠画），模糊后逐字符
        /// 以与填充一致的映射绘制，阴影跟随字浮/字缩；翻译行无动效，整行一条剪影。</summary>
        private void RebuildShadowResources(ICanvasResourceCreator resourceCreator)
        {
            var line = _currentLine;
            if (line?.PrimaryTextLayout is null) return;
            if (_shadowStrength <= 0) return;   // 阴影关闭：不建剪影资源，绘制 pass 同样跳过

            _shadowBrush = new CanvasSolidColorBrush(resourceCreator, DesktopLyricsShadow.Invert(_color));

            if (line.PrimaryRenderChars is { } chars && chars.Count > 0)
            {
                _charSlices = [];
                Rect? previousClip = null;
                for (int i = 0; i < chars.Count; i++)
                {
                    var ch = chars[i];
                    var clip = ch.LayoutRect;
                    if (clip.Height <= 0) continue;

                    // 命中测试盒退化（宽度为 0）时借用相邻字符的盒子合成，否则该字符
                    // 剪影为空——只剩邻字光晕盖住其左半边，表现为最后一个字符阴影缺失
                    // （尾字符为标点时墨水恰好偏左，缺陷被掩盖）
                    if (clip.Width <= 0)
                    {
                        if (previousClip is { } prev)
                            clip = new Rect(prev.Right, clip.Y, prev.Width, clip.Height);
                        else if (chars.Count > 1 && chars[1].LayoutRect.Width > 0)
                            clip = new Rect(Math.Max(0, chars[1].LayoutRect.X - chars[1].LayoutRect.Width), clip.Y, chars[1].LayoutRect.Width, clip.Height);
                        else
                            continue;
                    }

                    // 首尾分条向外再扩一个字宽：外侧没有邻居，外扩不会与相邻分条叠画，
                    // 用来兜住边缘字形墨水出框被裁切
                    if (i == 0)
                        clip = clip.Extend(clip.Width, 0, 0, 0);
                    if (i == chars.Count - 1)
                        clip = clip.Extend(0, 0, Math.Max(clip.Width, 4), 0);

                    var list = new CanvasCommandList(resourceCreator);
                    // 分条即字符命中测试盒：水平按字符列精确裁开（相邻列互不重叠，光晕不二次叠画）；
                    // 盒高本身就是整行高（含字形上伸/下延），垂直不再外扩，避免换行歌词时
                    // 把相邻子行的字形裁进本条造成双重阴影
                    using (var clSession = list.CreateDrawingSession())
                    using (clSession.CreateLayer(1f, clip))
                    {
                        clSession.DrawTextLayout(line.PrimaryTextLayout, line.PrimaryPosition, _shadowBrush);
                    }
                    _charSlices.Add(new CharShadowSlice(ch, list, CreateShadowBlur(list)));
                    previousClip = clip;
                }
            }
            else
            {
                _primaryWholeShadowList = new CanvasCommandList(resourceCreator);
                using (var clSession = _primaryWholeShadowList.CreateDrawingSession())
                {
                    clSession.DrawTextLayout(line.PrimaryTextLayout, line.PrimaryPosition, _shadowBrush);
                }
                _primaryWholeShadowBlur = CreateShadowBlur(_primaryWholeShadowList);
            }

            if (line.SecondaryTextLayout is { } secondary)
            {
                _secondaryShadowList = new CanvasCommandList(resourceCreator);
                using (var clSession = _secondaryShadowList.CreateDrawingSession())
                {
                    clSession.DrawTextLayout(secondary, line.SecondaryPosition, _shadowBrush);
                }
                _secondaryShadowBlur = CreateShadowBlur(_secondaryShadowList);
            }
        }

        private GaussianBlurEffect CreateShadowBlur(CanvasCommandList source)
        {
            // Soft 边界让模糊在剪影边缘自然衰减，不产生命令列表边界的硬截断
            return new GaussianBlurEffect
            {
                Source = source,
                BlurAmount = DesktopLyricsShadow.BlurSigma,
                BorderMode = EffectBorderMode.Soft,
            };
        }

        /// <summary>画当前行的软阴影剪影（须在填充文字之前、与文字同一 offsetY 变换下调用）。
        /// 画笔颜色每帧同步为当前文字色的反相：换色逐帧生效，无需重建剪影。
        /// 强度 ≤1 单层绘制；>1 叠加第二遍（整体不透明度上限 1，超出部分靠二次叠加加深光晕）。</summary>
        private void DrawTextShadow(CanvasDrawingSession ds)
        {
            if (_shadowStrength <= 0) return;   // 阴影关闭：直接跳过本绘制 pass
            var line = _currentLine;
            if (line is null || _shadowBrush is null) return;
            bool hasSlices = _charSlices is { } slices && slices.Count > 0;
            if (!hasSlices && _primaryWholeShadowBlur is null && _secondaryShadowBlur is null) return;

            float baseAlpha = (float)Math.Min(1.0, _shadowStrength);
            float extraAlpha = (float)Math.Max(0.0, _shadowStrength - 1.0);
            // 与 TextBlock 渲染器同源公式：DesktopLyricsShadow.SplitStrength(_shadowStrength)
            // （此处内联避免每帧元组解构），0–50 单层，50–100 第二遍叠加

            for (int pass = 0; pass < 2; pass++)
            {
                float alpha = pass == 0 ? baseAlpha : extraAlpha;
                if (alpha <= 0) break;
                _shadowBrush.Color = DesktopLyricsShadow.Invert(_color, alpha);

                if (hasSlices)
                {
                    // 主文本逐字符切片：dest 映射与共享库 DrawSingleCharacter 完全一致
                    // （缩放绕字符矩形中心 + 纵向浮动），外扩取块高容纳模糊光晕（同 glow 做法）
                    foreach (var slice in _charSlices!)
                    {
                        var rect = slice.Char.LayoutRect;
                        var sourceRect = new Rect(
                            rect.X + line.PrimaryPosition.X,
                            rect.Y + line.PrimaryPosition.Y,
                            rect.Width, rect.Height);
                        var destRect = sourceRect
                            .Scale(slice.Char.ScaleTransition.Value)
                            .AddY(slice.Char.FloatTransition.Value);
                        ds.DrawImage(slice.Blur, destRect.Extend(destRect.Height), sourceRect.Extend(sourceRect.Height));
                    }
                }
                else if (_primaryWholeShadowBlur is { } whole)
                {
                    ds.DrawImage(whole);
                }

                if (_secondaryShadowBlur is { } secondaryBlur)
                    ds.DrawImage(secondaryBlur);
            }
        }

        private void DisposeShadowResources()
        {
            // 先效果后源再画笔；Win2D Dispose 幂等，设备丢失后重复释放安全
            if (_charSlices is { } slices)
            {
                foreach (var slice in slices)
                {
                    slice.Blur.Dispose();
                    slice.List.Dispose();
                }
            }
            _charSlices = null;
            _primaryWholeShadowBlur?.Dispose();
            _primaryWholeShadowBlur = null;
            _primaryWholeShadowList?.Dispose();
            _primaryWholeShadowList = null;
            _secondaryShadowBlur?.Dispose();
            _secondaryShadowBlur = null;
            _secondaryShadowList?.Dispose();
            _secondaryShadowList = null;
            _shadowBrush?.Dispose();
            _shadowBrush = null;
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
