using AnimatedWin2dControls.Enums;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    /// <summary>
    /// 高级歌词渲染协调器。承载原 <see cref="AdvanceLyricsCanvasControl"/> 的渲染/输入/滚动过渡/Bus 逻辑，
    /// 面向外部传入的 <see cref="CanvasAnimatedControl"/> 与 <see cref="CanvasDrawingSession"/> 工作。
    /// 既被 AdvanceLyricsCanvasControl（独立场景）使用，也被合成宿主 NowPlayingCanvas（单 canvas）使用。
    /// </summary>
    public sealed partial class LyricsRenderCoordinator
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;
        public event EventHandler<Exception>? RenderError;

        /// <summary>宿主设置的目标画布。用于 Invalidate / Size / 指针捕获 / DPI 换算 / 帧率设置。</summary>
        public CanvasAnimatedControl? Canvas { get; set; }
        internal UIElement? TransparentInput { get; set; }
        private UIElement? InputElement => TransparentInput ?? Canvas;
        private Size HostSize => Canvas?.Size ?? default;

        private void InvalidateHost()
        {
            // The transparent image is redrawn directly by its composition clock.
            if (TransparentInput is null) Canvas?.Invalidate();
        }

        /// <summary>
        /// 歌词渲染区域（相对画布的矩形）。当 Width/Height &lt;= 0 时退化为整块画布，
        /// 用于独立 AdvanceLyricsCanvasControl（其画布本身已被布局到歌词单元格）。
        /// 合成宿主（全屏画布）通过设置此区域把歌词限制在右栏。
        /// </summary>
        public Rect LyricsRegion { get; set; }

        /// <summary>
        /// 可选的完整绘制表面，坐标与歌词区域相同。变换可以越过排版栏，最终裁剪仍使用此表面。
        /// </summary>
        public Rect LyricsViewport { get; set; }

        private double RegionX => LyricsRegion.Width > 0 ? LyricsRegion.X : 0;
        private double RegionY => LyricsRegion.Height > 0 ? LyricsRegion.Y : 0;
        private double RegionW => LyricsRegion.Width > 0 ? LyricsRegion.Width : HostSize.Width;
        private double RegionH => LyricsRegion.Height > 0 ? LyricsRegion.Height : HostSize.Height;

        /// <summary>宿主样式覆盖：字重（null = 跟随 LyricsSettingsBus，默认 700）。变更后调用 InvalidateStyle()。</summary>
        public int? FontWeightOverride { get; set; }

        /// <summary>宿主样式覆盖变更后触发重排与重绘（重建文本布局/几何/描边缓存）。</summary>
        public void InvalidateStyle()
        {
            _layoutDirty = true;
            InvalidateHost();
        }

        /// <summary>判断画布坐标系的点是否落在歌词绘制区域内。
        /// 独立控件（LyricsRegion 留空时 RegionW/H 退化为整个画布）下必返回 true，零行为变化。</summary>
        private bool IsPointerInLyricsRegion(Point canvasPos)
        {
            return canvasPos.X >= RegionX
                && canvasPos.X < RegionX + RegionW
                && canvasPos.Y >= RegionY
                && canvasPos.Y < RegionY + RegionH;
        }

        private List<RenderLyricsLine> _renderLines = [];
        // The UI publishes managed snapshots only. Native caches belong to the render loop;
        // shutdown waits for its current operation before releasing those caches.
        private readonly Lock _renderGate = new();
        private readonly Lock _pendingGate = new();
        private List<RenderLyricsLine>? _pendingLines;
        private IList<LyricLine>? _lastSource;
        private bool _pendingAnimate;
        private bool _pendingClear;
        private bool _fadingOut;
        private bool _preparingEntrance;
        private float _lyricsOpacity = 1;
        private const float LyricFadeOutMs = 150;
        private const float LyricFadeInMs = 200;
        private readonly Windows.UI.ViewManagement.UISettings _motionSettings = new();
        private bool _layoutDirty = true;
        private volatile bool _shutdown;
        private int _currentLineIndex = -1;
        private int _lastCurrentLineIndex = -1;

        private LyricsSynchronizer _synchronizer = new();
        private LyricsAnimator _animator = new();
        private LyricsLineRenderer _lineRenderer = new();
        private EdgeFadeMaskRenderer _edgeFadeMask = new();

        private ValueTransition<double> _canvasYScrollTransition;
        private ValueTransition<double> _mouseYScrollTransition;
        private double _pendingMouseScrollY;

        private double _targetScrollY;
        private double _smoothedScrollY;
        private double _lastTargetScrollY;
        // Capture the entrance origin once; displacement and later effect updates share it.
        private LyricScrollTiming _lineScrollTiming;
        private bool _userScrolling;
        private bool _isUserScrollingChanged;
        private double _userScrollCooldownSec;
        private const double UserScrollCooldown = 2.5;

        private double _internalTimeMs;
        private double _lastExternalTimeMs;
        private double _lastScrollPositionMs = double.NaN;
        private const double SyncThresholdMs = 250.0;

        public double AutoScrollSpeed = 4.0;
        public double UserScrollReturnSpeed = 12.0;
        public double WheelScrollPixels = 80.0;

        private int _hoveredLineIndex = -1;
        private bool _isPointerOnSurface;
        private Point _lastMousePos;
        private bool _hoverDirty;
        private bool _isDarkTheme;
        private CanvasSolidColorBrush? _hdrHoverBrush;
        private CanvasDevice? _hdrHoverDevice;
        private float _hdrHoverWhite = -1;

        private int _cachedVisibleStart;
        private int _cachedVisibleEnd;
        private Rect _edgeFadeBounds;

        /// <summary>
        /// 单调递增的帧版本号。每帧 <see cref="OnUpdate"/> 自增后传入
        /// <see cref="LyricsAnimator.UpdateLines"/>；动画器用它识别"上一帧不在动画范围内"的行，
        /// 在重新进入时强制重算 Blur/Scale/Opacity 等距离效果（不依赖
        /// <c>UnplayedPrimaryOpacityTransition.Value == 0</c> 这类脆弱的浮点嗅探）。
        /// </summary>
        private int _animationVersion;

        private Color _playedColor = Colors.White;
        private Color _unplayedColor = Color.FromArgb(80, 255, 255, 255);

        private double _cachedLyricsFontSize = 36.0;
        private string _cachedFontFamilyName = "Segoe UI";
        private Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment _cachedLyricsTextAlignment = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left;
        private double _cachedCurrentPlayingTimeMs = 0.0;
        private double _cachedOffsetMs;
        private bool _cachedIsPlaying;
        private double _cachedLyricsBlurAmount = 4.0;
        private double _cachedGlowAmount;
        private double _cachedCharFloatAmount;
        private double _cachedCharScaleAmount;
        private double _cachedLongSyllableThreshold = 500.0;
        private double _cachedScrollSensitivity = 1.0;
        private bool _cachedIsFadeOutEnabled = true;
        private bool _cachedIsOutOfSightEnabled = true;
        private double _cachedUnplayedOpacity = 0.5;
        private double _cachedTranslatedOpacity = 0.6;
        private bool _cachedShowTranslation = true;
        private bool _cachedShowPronunciation = true;
        private bool _cachedIsLyricsEdgeFeatheringEffectEnabled = true;
        private bool _cachedIsFanLyricsEnabled;
        private int _cachedFanLyricsAngle = 30;
        private bool _cachedIs3DLyricsEnabled;
        private bool _cachedIsAuto3DLyricsEnabled;
        private int _cachedLyrics3DXAngle;
        private int _cachedLyrics3DYAngle;
        private int _cachedLyrics3DZAngle;
        private int _cachedLyrics3DDepth = 800;
        private WordByWordEffectMode _cachedWordByWordEffectMode;
        private double _cachedStrokeWidth;
        private int _cachedFontWeight = 700;
        private EasingType _cachedScrollEasingType = EasingType.FlowWave;
        private EaseMode _cachedScrollEasingMode = EaseMode.FlowWave;
        private bool UsesLineScroll => _cachedScrollEasingType == EasingType.FlowWave
            || _cachedScrollEasingMode == EaseMode.FlowWave;
        private double _cachedScrollDurationMs = 500;
        private double _cachedPlayingLineTopOffset = 0.35;
        private double _cachedTargetFrameRate = 120.0;

        public double TargetFrameRate => _cachedTargetFrameRate;

        public LyricsRenderCoordinator()
        {
            _canvasYScrollTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
            _mouseYScrollTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
        }

        // ── 生命周期 ──────────────────────────────────────────────────────────

        /// <summary>订阅 Bus 并请求一次同步。owner 应在 Loaded 时调用。</summary>
        public void Attach()
        {
            if (_shutdown) return;
            // 先设初始默认色，再订阅并请求同步；Request 会同步回灌真实 isDark，
            // 必须早于此默认，避免把正确颜色覆盖回浅色默认（导致暗色模式显示黑字）。
            OnIsDarkChanged(false);

            TimeProgressBus.CurrentPlayingTimeChanged += OnCurrentPlayingTimeChanged;
            IsPlayingBus.Changed += OnIsPlayingChanged;
            OffsetMsBus.Changed += OnOffsetMsChanged;
            LyricsFontSizeBus.Changed += OnLyricsFontSizeChanged;
            UILyricsBus.PlaybackChanged += OnUILyricsChanged;
            LyricsSettingsBus.SyncRequested += OnLyricsSettingsChanged;
            LyricsSyncRequestBus.Request();
        }

        public void Detach()
        {
            TimeProgressBus.CurrentPlayingTimeChanged -= OnCurrentPlayingTimeChanged;
            IsPlayingBus.Changed -= OnIsPlayingChanged;
            OffsetMsBus.Changed -= OnOffsetMsChanged;
            LyricsFontSizeBus.Changed -= OnLyricsFontSizeChanged;
            UILyricsBus.PlaybackChanged -= OnUILyricsChanged;
            LyricsSettingsBus.SyncRequested -= OnLyricsSettingsChanged;
        }

        public void ResetTimeSync() => _lastExternalTimeMs = 0;

        public void OnCreateResources()
        {
            lock (_renderGate)
            {
                if (_shutdown) return;
                _edgeFadeMask.Reset();
                _edgeFadeBounds = default;
                ReleaseLyricsPerspective();
                _layoutDirty = true;
            }
        }

        internal void ResetDeviceResources()
        {
            lock (_renderGate)
            {
                DisposeRenderLines();
                _lineRenderer.ClearSharedResources();
                ReleaseHoverBrush();
                OnCreateResources();
            }
        }

        public void PrepareForShutdown()
        {
            lock (_renderGate)
            {
                lock (_pendingGate)
                {
                    if (_shutdown) return;
                    _shutdown = true;
                    _lastSource = null;
                    _pendingClear = false;
                    _fadingOut = false;
                    _preparingEntrance = false;
                    _lyricsOpacity = 1;
                    _pendingLines = null; // Unconsumed snapshots have no native resources.
                }
                Detach();
                DisposeRenderLines();
                _renderLines = [];
                _lineRenderer.Line = null;
                _lineRenderer.ClearSharedResources();
                _edgeFadeMask.Dispose();
                ReleaseLyricsPerspective();
                ReleaseHoverBrush();
                Canvas = null;
            }
        }

        /// <summary>
        /// 释放 x:Load 卸载期间的设备资源，但保留协调器可再次 Attach。
        /// x:Load 可能复用同一个控件实例，不能把这种暂时离树当成最终 shutdown。
        /// </summary>
        public void ReleaseForUnload()
        {
            lock (_renderGate)
            {
                if (_shutdown) return;
                lock (_pendingGate)
                {
                    _lastSource = null;
                    _pendingClear = false;
                    _fadingOut = false;
                    _preparingEntrance = false;
                    _lyricsOpacity = 1;
                    _pendingLines = null;
                }

                Detach();
                DisposeRenderLines();
                _renderLines = [];
                _lineRenderer.Line = null;
                _edgeFadeMask.Reset();
                _edgeFadeBounds = default;
                ReleaseLyricsPerspective();
                ReleaseHoverBrush();
                Canvas = null;
                _layoutDirty = true;

                _currentLineIndex = -1;
                _lastCurrentLineIndex = -1;
            }
        }

        public bool HasLyrics => _renderLines.Count > 0;

        // ── Bus Handlers ──────────────────────────────────────────────────────

        private void OnCurrentPlayingTimeChanged(long totalMs) => _cachedCurrentPlayingTimeMs = totalMs;
        private void OnIsPlayingChanged(bool value) => _cachedIsPlaying = value;
        private void OnOffsetMsChanged(double value) => _cachedOffsetMs = value;

        private void OnLyricsFontSizeChanged(double value)
        {
            _cachedLyricsFontSize = value;
            _layoutDirty = true;
            InvalidateHost();
        }



        private void OnLyricsSettingsChanged(LyricsSettingsBus.Settings s)
        {
            _cachedFontFamilyName = s.FontFamilyName;
            _cachedLyricsTextAlignment = s.LyricsTextAlignment;
            _cachedScrollSensitivity = s.ScrollSensitivity;
            _cachedLyricsBlurAmount = s.LyricsBlurAmount;
            _cachedGlowAmount = double.IsFinite(s.GlowAmount) ? Math.Clamp(s.GlowAmount, 0, 10) : 0;
            _cachedCharFloatAmount = s.CharFloatAmount;
            _cachedCharScaleAmount = s.CharScaleAmount;
            _cachedLongSyllableThreshold = s.LongSyllableThreshold;
            _cachedIsFadeOutEnabled = s.IsFadeOutEnabled;
            _cachedIsOutOfSightEnabled = s.IsOutOfSightEnabled;
            _cachedUnplayedOpacity = s.UnplayedOpacity;
            _cachedTranslatedOpacity = s.TranslatedOpacity;
            _cachedShowTranslation = s.ShowTranslation;
            _cachedShowPronunciation = s.ShowPronunciation;
            _cachedIsLyricsEdgeFeatheringEffectEnabled = s.IsLyricsEdgeFeatheringEffectEnabled;
            _cachedIsFanLyricsEnabled = s.IsFanLyricsEnabled;
            _cachedFanLyricsAngle = s.FanLyricsAngle;
            _cachedIs3DLyricsEnabled = s.Is3DLyricsEnabled;
            _cachedIsAuto3DLyricsEnabled = s.IsAuto3DLyricsEnabled;
            _cachedLyrics3DXAngle = s.Lyrics3DXAngle;
            _cachedLyrics3DYAngle = s.Lyrics3DYAngle;
            _cachedLyrics3DZAngle = s.Lyrics3DZAngle;
            _cachedLyrics3DDepth = s.Lyrics3DDepth;
            _cachedWordByWordEffectMode = s.WordByWordEffectMode;
            _cachedStrokeWidth = s.StrokeWidth;
            _cachedFontWeight = s.FontWeight;
            _cachedScrollEasingType = s.ScrollEasingType;
            _cachedScrollEasingMode = s.ScrollEasingMode;
            _cachedPlayingLineTopOffset = s.PlayingLineTopOffset;
            _cachedTargetFrameRate = s.TargetFrameRate;
            if (Canvas is not null)
                Canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / s.TargetFrameRate);

            _isDarkTheme = s.IsDark;
            if (s.IsCustomColorEnabled)
            {
                _playedColor = s.LyricsCustomColor;
                _unplayedColor = s.LyricsCustomColor;
            }
            else if (_isDarkTheme)
            {
                _playedColor = Colors.White;
                _unplayedColor = Colors.White;
            }
            else
            {
                _playedColor = Color.FromArgb(255, 0, 0, 0);
                _unplayedColor = Color.FromArgb(255, 0, 0, 0);
            }

            _layoutDirty = true;
            InvalidateHost();
        }

        private void OnUILyricsChanged(IList<LyricLine>? newLyrics, bool animateChange)
        {
            lock (_pendingGate)
            {
                if (_shutdown || (newLyrics is not null && ReferenceEquals(newLyrics, _lastSource))) return;
            }
            bool animate = animateChange && _motionSettings.AnimationsEnabled;
            var newLines = new List<RenderLyricsLine>(newLyrics?.Count ?? 0);
            if (newLyrics != null && newLyrics.Count > 0)
            {
                for (int i = 0; i < newLyrics.Count; i++)
                {
                    var lyricLine = newLyrics[i];
                    var renderLine = new RenderLyricsLine();
                    renderLine.LoadFromLyricLine(lyricLine, lyricLine.EndMs);
                    newLines.Add(renderLine);
                }
            }

            lock (_pendingGate)
            {
                if (_shutdown) return;
                // A newer snapshot can replace an unconsumed one without disposing Win2D objects.
                _lastSource = newLyrics;
                _pendingLines = newLines;
                _pendingAnimate = animate;
                _pendingClear = animate && (_pendingClear || newLines.Count == 0);
            }
            InvalidateHost();
        }

        // Called only at a render callback boundary while holding _renderGate.
        private bool AdvanceLyricsTransition(TimeSpan elapsed)
        {
            List<RenderLyricsLine>? next;
            lock (_pendingGate)
            {
                next = _pendingLines;
                if (next is null)
                {
                    if (!_preparingEntrance && _lyricsOpacity < 1)
                        _lyricsOpacity = Math.Min(1, _lyricsOpacity + (float)(elapsed.TotalMilliseconds / LyricFadeInMs));
                    return false;
                }

                // Freeze the outgoing lines at their last playback position. Later selections
                // can replace the pending snapshot without restarting their fade.
                if (_pendingClear && _renderLines.Count != 0 && _lyricsOpacity > 0)
                {
                    _fadingOut = true;
                    _lyricsOpacity = Math.Max(0, _lyricsOpacity - (float)(elapsed.TotalMilliseconds / LyricFadeOutMs));
                    if (_lyricsOpacity > 0) return false;
                }
                bool entering = next.Count != 0 && (_renderLines.Count == 0 || _fadingOut);
                _preparingEntrance = _pendingAnimate && entering;
                if (!_pendingAnimate || next.Count == 0) _lyricsOpacity = 1;
                else if (entering) _lyricsOpacity = 0;
                _pendingLines = null;
                _pendingClear = false;
                _fadingOut = false;
            }

            var previous = _renderLines;
            _renderLines = next;
            _lineRenderer.Line = null;
            DisposeLineList(previous);
            _currentLineIndex = -1;
            _lastCurrentLineIndex = -1;
            _hoveredLineIndex = -1;
            _cachedVisibleStart = -1;
            _cachedVisibleEnd = -1;
            _lastExternalTimeMs = double.NegativeInfinity;
            _lastScrollPositionMs = double.NaN;
            _canvasYScrollTransition.JumpTo(0);
            _mouseYScrollTransition.JumpTo(0);
            _pendingMouseScrollY = 0;
            _targetScrollY = 0;
            _smoothedScrollY = 0;
            _lastTargetScrollY = 0;
            _userScrolling = false;
            _isUserScrollingChanged = false;
            _userScrollCooldownSec = 0;
            _layoutDirty = true;
            return true;
        }

        private void OnIsDarkChanged(bool isDark)
        {
            _isDarkTheme = isDark;
            if (isDark)
            {
                _playedColor = Colors.White;
                _unplayedColor = Colors.White;
            }
            else
            {
                _playedColor = Color.FromArgb(255, 0, 0, 0);
                _unplayedColor = Color.FromArgb(255, 0, 0, 0);
            }
            InvalidateHost();
        }

        // ── 布局 ──────────────────────────────────────────────────────────────

        private double _lastLayoutWidth;
        private double _lastLayoutHeight;

        private void EnsureLayout(ICanvasResourceCreator resourceCreator)
        {
            if (_renderLines.Count == 0) return;
            var layoutLines = _renderLines;

            double regionW = RegionW;
            double regionH = RegionH;

            if (!_layoutDirty && Math.Abs(regionW - _lastLayoutWidth) < 0.5 && Math.Abs(regionH - _lastLayoutHeight) < 0.5)
                return;

            _lastLayoutWidth = regionW;
            _lastLayoutHeight = regionH;

            int originalFontSize = (int)_cachedLyricsFontSize;
            int translatedFontSize = (int)(_cachedLyricsFontSize * 0.7);
            // 桌面歌词等宿主可覆盖描边宽度与字重（null = 跟随总线值）
            int effectiveStrokeWidth = (int)_cachedStrokeWidth;
            int fontWeight = FontWeightOverride ?? _cachedFontWeight;

            try
            {
                LyricsLayoutManager.MeasureAndArrange(
                    resourceCreator,
                    layoutLines,
                    originalFontSize,
                    translatedFontSize,
                    _cachedFontFamilyName,
                    _cachedLyricsTextAlignment,
                    regionW,
                    regionH,
                    effectiveStrokeWidth,
                    fontWeight,
                    _cachedShowTranslation,
                    _cachedShowPronunciation,
                    _cachedCharFloatAmount > 0, deferRenderChars: true);

                if (_currentLineIndex >= 0)
                {
                    var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(layoutLines, _currentLineIndex);
                    if (targetScroll.HasValue)
                    {
                        _canvasYScrollTransition.JumpTo(targetScroll.Value);
                        _mouseYScrollTransition.JumpTo(0);
                        _pendingMouseScrollY = 0;
                        _targetScrollY = targetScroll.Value;
                        _smoothedScrollY = targetScroll.Value;
                        _lastTargetScrollY = targetScroll.Value;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                DisposeRenderLineCaches();
                _layoutDirty = true;
            }
        }

        // ── Update ──────────────────────────────────────────────────────────────

        public void OnUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
            => OnUpdate(sender, args.Timing.ElapsedTime);

        /// <summary>消费帧时间值；原始 ABI 宿主不需要构造托管 Update 事件参数。</summary>
        public void OnUpdate(ICanvasResourceCreator sender, TimeSpan elapsedTime)
        {
            lock (_renderGate)
            {
                if (_shutdown) return;
                AdvanceLyricsTransition(elapsedTime);
                if (!_fadingOut) UpdateCore(sender, elapsedTime);
            }
        }

        private void UpdateCore(ICanvasResourceCreator sender, TimeSpan elapsedTime)
        {
            EnsureLayout(sender);
            var lines = _renderLines;
            if (lines.Count == 0) return;

            bool isSecondaryLayoutAnimating = false;
            foreach (var line in lines)
            {
                isSecondaryLayoutAnimating |= line.IsSecondaryLayoutTransitioning;
                line.UpdateSecondaryTransitions(elapsedTime);
                // Include the completion frame: positions are arranged from the
                // newly settled reveal values before opacity distance is sampled.
                isSecondaryLayoutAnimating |= line.IsSecondaryLayoutTransitioning;
            }
            LyricsLayoutManager.ArrangeCurrentHeights(
                lines,
                (int)_cachedLyricsFontSize,
                _cachedLyricsTextAlignment,
                RegionW);

            // 单调自增，每帧一格。LyricsAnimator 内部用 animationVersion - 1
            // 来识别"上一帧不在动画范围内"的行，强制重算距离效果。
            _animationVersion++;

            bool hasPlaybackClock = TimeProgressBus.TryReadClock(out long clockMs);
            double externalTimeMs = hasPlaybackClock ? clockMs : _cachedCurrentPlayingTimeMs;

            bool isPrimaryPlayingLineChanged = false;
            double currentTimeMs = 0;

            if (_cachedIsPlaying)
            {
                if (hasPlaybackClock || Math.Abs(externalTimeMs - _lastExternalTimeMs) > SyncThresholdMs)
                {
                    _internalTimeMs = externalTimeMs;
                }
                else
                {
                    _internalTimeMs += elapsedTime.TotalMilliseconds;
                }
                _lastExternalTimeMs = externalTimeMs;

                currentTimeMs = _internalTimeMs - _cachedOffsetMs;

                int newIndex = _synchronizer.GetCurrentLineIndex(currentTimeMs, lines);
                _lastCurrentLineIndex = _currentLineIndex;
                if (newIndex != _currentLineIndex)
                    _currentLineIndex = newIndex;

                isPrimaryPlayingLineChanged = _lastCurrentLineIndex != _currentLineIndex;

                if (isPrimaryPlayingLineChanged || _layoutDirty)
                {
                    _canvasYScrollTransition.SetInterpolator(
                        EasingHelper.GetInterpolatorByEasingType<double>(
                            _cachedScrollEasingType == EasingType.FlowWave
                                && _cachedScrollEasingMode != EaseMode.In && _cachedScrollEasingMode != EaseMode.InOut
                                ? EasingType.Linear : _cachedScrollEasingType,
                            _cachedScrollEasingMode));
                }
            }
            else
            {
                _internalTimeMs = externalTimeMs;
                _lastExternalTimeMs = externalTimeMs;
                currentTimeMs = _internalTimeMs - _cachedOffsetMs;

                int newIndex = _synchronizer.GetCurrentLineIndex(currentTimeMs, lines);
                _lastCurrentLineIndex = _currentLineIndex;
                if (newIndex != _currentLineIndex)
                    _currentLineIndex = newIndex;

                isPrimaryPlayingLineChanged = _lastCurrentLineIndex != _currentLineIndex;
            }

            double dt = elapsedTime.TotalSeconds;
            bool isScrollSeek = double.IsFinite(_lastScrollPositionMs)
                && Math.Abs(currentTimeMs - _lastScrollPositionMs - (_cachedIsPlaying ? dt * 1000 : 0)) > SyncThresholdMs;
            _lastScrollPositionMs = currentTimeMs;

            if (!_userScrolling && (_cachedIsPlaying || UsesLineScroll || isSecondaryLayoutAnimating)
                && _currentLineIndex >= 0 && _currentLineIndex < lines.Count)
            {
                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(lines, _currentLineIndex);
                if (targetScroll.HasValue)
                {
                    // Secondary tracks change the row geometry every frame. Keep
                    // the playing line at the configured viewport anchor while
                    // that reflow is in progress; letting the normal scroll
                    // easing chase a moving target makes the line visibly lag
                    // behind and then overshoot when translation/pronunciation
                    // is toggled.
                    if (_layoutDirty || isSecondaryLayoutAnimating)
                    {
                        _canvasYScrollTransition.JumpTo(targetScroll.Value);
                        _targetScrollY = targetScroll.Value;
                        _smoothedScrollY = targetScroll.Value;
                    }
                    else if (_cachedScrollEasingMode == EaseMode.Continuous && !UsesLineScroll)
                    {
                        _canvasYScrollTransition.SetDurationMs(300);
                        _canvasYScrollTransition.Start(targetScroll.Value);
                    }
                    else if (targetScroll.Value != _lastTargetScrollY)
                    {
                        _canvasYScrollTransition.SetDurationMs(_cachedScrollDurationMs);
                        _canvasYScrollTransition.Start(targetScroll.Value);
                    }
                    _lastTargetScrollY = targetScroll.Value;
                }
            }

            _canvasYScrollTransition.Update(elapsedTime);
            _mouseYScrollTransition.Update(elapsedTime);

            _smoothedScrollY = _canvasYScrollTransition.Value;

            if (_userScrollCooldownSec > 0)
                _userScrollCooldownSec -= dt;

            if (_userScrolling && _userScrollCooldownSec <= 0)
            {
                _isUserScrollingChanged = true;
                _userScrolling = false;
                _mouseYScrollTransition.Start(0);
                _pendingMouseScrollY = 0;
            }

            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;

            double canvasHeight = RegionH > 0 ? RegionH : 400;
            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;

            LyricScrollTiming? scrollTiming = null;
            if (UsesLineScroll)
                scrollTiming = UpdateLineScrolls(
                    dt,
                    canvasHeight,
                    isPrimaryPlayingLineChanged,
                    isScrollSeek,
                    isSecondaryLayoutAnimating && !_userScrolling);

            bool projectionChanged = UpdateLyricsProjection(elapsedTime);
            var visibleRange = CalculateVisibleLyricsRange(combinedScroll, canvasHeight, playingLineTopOffsetFactor);

            _cachedVisibleStart = visibleRange.Start;
            _cachedVisibleEnd = visibleRange.End;

            // Row heights must be exact before a seek. Character and drawing resources are
            // only needed for the same three overscan lines on either side as the draw pass.
            int first = Math.Max(0, visibleRange.Start - 3);
            int last = Math.Min(lines.Count - 1, visibleRange.End + 3);
            if (!_preparingEntrance || !_layoutDirty)
                PrepareVisibleLines(sender, first, last);

            // 动画范围需与 DrawLyricsContent 的绘制范围一致（[visibleStart-3, visibleEnd+3]），
            // 否则处于缓冲区、但仍被绘制出来的行会脱离动画器：它们携带的 transition 残留值
            // （如 Blur=0）在重新滑回动画区时不会被刷新，表现为"露半行却无模糊/淡出"。
            // 此处 endIndex 传 visibleEnd+2：UpdateLines 内部会再 +1（safeEnd），恰好对齐 visibleEnd+3。
            int animStart = _userScrolling ? 0 : visibleRange.Start - 3;
            int animEnd = _userScrolling ? lines.Count - 1 : visibleRange.End + 2;

            _animator.UpdateLines(
                lines,
                animStart,
                animEnd,
                _currentLineIndex,
                0,
                canvasHeight,
                combinedScroll,
                playingLineTopOffsetFactor,
                _cachedLyricsBlurAmount > 0,
                _cachedIsOutOfSightEnabled,
                _cachedIsFadeOutEnabled,
                _cachedUnplayedOpacity,
                1.0,
                _cachedTranslatedOpacity,
                _cachedGlowAmount > 0,
                _cachedGlowAmount,
                _cachedLongSyllableThreshold,
                _cachedCharFloatAmount > 0,
                _cachedCharFloatAmount,
                450,
                _cachedCharScaleAmount > 0,
                _cachedCharScaleAmount / 100.0,
                _cachedLongSyllableThreshold,
                _cachedLyricsBlurAmount,
                _canvasYScrollTransition,
                elapsedTime,
                _userScrolling,
                _isUserScrollingChanged,
                _layoutDirty,
                isPrimaryPlayingLineChanged,
                currentTimeMs,
                _animationVersion,
                scrollTiming,
                isScrollSeek,
                _cachedScrollEasingMode == EaseMode.FlowWave && !_userScrolling,
                isSecondaryLayoutAnimating,
                wordByWordEffectMode: _cachedWordByWordEffectMode);

            if (_cachedIsFanLyricsEnabled)
            {
                UpdateFanAngles(first, last, canvasHeight, playingLineTopOffsetFactor, scrollTiming,
                    elapsedTime, isScrollSeek || _layoutDirty || isSecondaryLayoutAnimating);
            }

            _layoutDirty = false;
            _isUserScrollingChanged = false;

            _hoverDirty |= projectionChanged || HasTransformedLyrics;
            HandleHoverUpdates(combinedScroll, canvasHeight, playingLineTopOffsetFactor);
        }

        private LyricScrollTiming UpdateLineScrolls(
            double seconds,
            double canvasHeight,
            bool lineChanged,
            bool isSeek,
            bool preserveSecondaryLayoutAnchor)
        {
            double target = _lastTargetScrollY;
            double duration = Math.Max(0, _cachedScrollDurationMs / 1000.0);
            bool spring = _cachedScrollEasingType == EasingType.FlowWave
                && _cachedScrollEasingMode != EaseMode.In && _cachedScrollEasingMode != EaseMode.InOut;
            bool regularAdvance = lineChanged && !isSeek && _currentLineIndex == _lastCurrentLineIndex + 1
                && _lastCurrentLineIndex >= 0;
            double interval = 0;
            // Tweens fit the next entrance; springs keep stable stiffness across short lines.
            if (regularAdvance && _currentLineIndex + 1 < _renderLines.Count)
            {
                interval = (_renderLines[_currentLineIndex + 1].StartMs
                    - _renderLines[_currentLineIndex].StartMs) / 1000.0;
                if (!spring && interval > 0) duration = Math.Min(duration, interval * 0.85);
            }
            bool stagger = _cachedScrollEasingMode == EaseMode.FlowWave && regularAdvance
                && !_userScrolling && !_isUserScrollingChanged;
            if (_layoutDirty || lineChanged || isSeek || _isUserScrollingChanged
                || _renderLines[0].ScrollMotion.TargetValue != target)
            {
                // Match BetterLyrics' visible-range origin, without including the glyph buffer rows.
                int firstVisible = stagger
                    ? LyricsLayoutManager.CalculateVisibleRange(_renderLines,
                        _smoothedScrollY + _mouseYScrollTransition.Value, 0, canvasHeight, canvasHeight,
                        _cachedPlayingLineTopOffset).Start
                    : -1;
                _lineScrollTiming = new LyricScrollTiming(duration, interval,
                    stagger ? Math.Max(0, firstVisible) : -1, spring);
            }
            if (preserveSecondaryLayoutAnchor)
            {
                // Each row has its own offset in FlowWave mode. Snap all of
                // them to the moving layout target so the reflow itself is the
                // only motion during a secondary-track toggle.
                foreach (var line in _renderLines)
                    line.ScrollMotion.JumpTo(target);
                _hoverDirty = true;
                return _lineScrollTiming;
            }
            var interpolator = EasingHelper.GetInterpolatorByEasingType<double>(
                _cachedScrollEasingType, _cachedScrollEasingMode);
            if (_layoutDirty)
            {
                foreach (var line in _renderLines) line.ScrollMotion.JumpTo(target);
            }
            for (int i = 0; i < _renderLines.Count; i++)
            {
                var motion = _renderLines[i].ScrollMotion;
                if (!_userScrolling)
                {
                    var timing = _lineScrollTiming.GetLineTiming(i);
                    motion.Start(target, timing.Duration, timing.Delay, spring, interpolator,
                        continueCurrentMotionDuringDelay: spring);
                }
                motion.Update(seconds);
            }
            // A stationary pointer can move over a different line while the lines scroll.
            _hoverDirty = true;
            return _lineScrollTiming;
        }

        private void HandleHoverUpdates(double combinedScroll, double canvasHeight, double playingLineTopOffsetFactor)
        {
            if (!_hoverDirty) return;
            _hoverDirty = false;

            var lines = _renderLines;
            if (lines.Count == 0) return;

            if (_isPointerOnSurface)
            {
                _hoveredLineIndex = FindHoveredLyricsLine(_lastMousePos, combinedScroll, canvasHeight,
                    playingLineTopOffsetFactor);
            }
            else
            {
                _hoveredLineIndex = -1;
            }
        }

        // ── Draw（不负责清屏，由宿主决定清屏色） ─────────────────────────────────

        public void OnDraw(ICanvasAnimatedControl sender, CanvasDrawingSession ds)
        {
            using var scope = CanvasDrawingSessionScope.Borrow(ds);
            OnDraw(sender, scope.Context);
        }

        /// <summary>Draws production lyric state using the scoped ABI context.</summary>
        public void OnDraw(ICanvasResourceCreator sender, CanvasDrawContext ds, PlaybackHdrColor hdr = default,
            bool consumePendingLyrics = true)
        {
            lock (_renderGate)
            {
                if (_shutdown) return;
                // Invalidate may request a draw while the animation is paused, without Update.
                if (consumePendingLyrics && AdvanceLyricsTransition(TimeSpan.Zero)) UpdateCore(sender, TimeSpan.Zero);
                if (_renderLines.Count > 0 && _lyricsOpacity > 0) DrawCore(sender, ds, hdr);
            }
        }

        private void PrepareVisibleLines(ICanvasResourceCreator sender, int first, int last)
        {
            // While the lyric layer is still transparent, spread native cache construction
            // across frames. Once visible, newly exposed rows are ready for this very draw.
            long start = _preparingEntrance ? Stopwatch.GetTimestamp() : 0;
            if ((_currentLineIndex < first || _currentLineIndex > last) && (uint)_currentLineIndex < (uint)_renderLines.Count)
                EnsureChars(_renderLines[_currentLineIndex]);
            for (int i = first; i <= last; i++)
            {
                var line = _renderLines[i];
                EnsureChars(line);
                if (!_preparingEntrance || line.CachedFill is not null) continue;
                line.EnsureCaches(sender, _cachedStrokeWidth);
                if (i < last && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 0.75) return;
            }
            _preparingEntrance = false;

            void EnsureChars(RenderLyricsLine line)
            {
                if (!line.RenderCharsReady)
                    line.RecreateRenderChars((int)_cachedStrokeWidth, _cachedCharFloatAmount > 0);
            }
        }

        private void DrawCore(ICanvasResourceCreator sender, CanvasDrawContext ds, PlaybackHdrColor hdr)
        {
            double rw = RegionW;
            double rh = RegionH;
            if (rw <= 0 || rh <= 0) return;

            var layout = new Rect(RegionX, RegionY, rw, rh);
            var viewport = GetLyricsPaintBounds(layout);
            var sourceBounds = GetLyricsSourceBounds(layout, viewport);
            using var clip = ds.CreateLayer(_lyricsOpacity, _lyricsProjection.IsIdentity ? sourceBounds : viewport);
            if (_lyricsProjection.IsIdentity)
            {
                DrawFeatheredLyrics(sender, ds, layout, sourceBounds, hdr);
                return;
            }

            if (!_hasInverseLyricsProjection) return;

            // Keep the source plane finite while allowing it to span the
            // surface. The original lyric column only defines the layout.
            using var commands = new CanvasCommandList(sender);
            using (var session = commands.CreateDrawingSession())
            using (var scope = CanvasDrawingSessionScope.Borrow(session))
            {
                var source = scope.Context;
                using var sourceClip = source.CreateLayer(1, sourceBounds);
                DrawFeatheredLyrics(sender, source, layout, sourceBounds, hdr);
            }

            var effect = _lyricsPerspectiveEffect ??= new Microsoft.Graphics.Canvas.Effects.Transform3DEffect();
            effect.TransformMatrix = _lyricsProjection;
            effect.Source = commands;
            try
            {
                ds.DrawImage(effect);
            }
            finally
            {
                effect.Source = null;
            }
        }

        private void DrawFeatheredLyrics(ICanvasResourceCreator sender, CanvasDrawContext ds,
            Rect layout, Rect bounds, PlaybackHdrColor hdr)
        {
            if (_cachedIsLyricsEdgeFeatheringEffectEnabled)
            {
                if (_edgeFadeBounds != bounds)
                {
                    // Scale the fade with the visible lyric area, leaving the center at full opacity.
                    float height = (float)bounds.Height;
                    float edge = Math.Min(0.25f, Math.Clamp(height * 0.18f, 16f, 160f) / height);
                    var quarter = Color.FromArgb(40, 255, 255, 255);
                    var half = Color.FromArgb(128, 255, 255, 255);
                    var threeQuarters = Color.FromArgb(215, 255, 255, 255);
                    CanvasGradientStop[] stops =
                    [
                        new() { Color = Colors.Transparent },
                        new() { Position = edge * 0.25f, Color = quarter },
                        new() { Position = edge * 0.5f, Color = half },
                        new() { Position = edge * 0.75f, Color = threeQuarters },
                        new() { Position = edge, Color = Colors.White },
                        new() { Position = 1 - edge, Color = Colors.White },
                        new() { Position = 1 - edge * 0.75f, Color = threeQuarters },
                        new() { Position = 1 - edge * 0.5f, Color = half },
                        new() { Position = 1 - edge * 0.25f, Color = quarter },
                        new() { Position = 1, Color = Colors.Transparent }
                    ];
                    _edgeFadeMask.Update(sender, bounds, stops, true);
                    _edgeFadeBounds = bounds;
                }

                if (_edgeFadeMask.Brush is { } mask)
                {
                    using var feather = ds.CreateLayer(mask);
                    DrawLyricsContent(sender, ds, layout.X, layout.Y, layout.Width, layout.Height, hdr);
                    return;
                }
            }

            DrawLyricsContent(sender, ds, layout.X, layout.Y, layout.Width, layout.Height, hdr);
        }

        private void DrawLyricsContent(ICanvasResourceCreator sender, CanvasDrawContext ds, double rx, double ry, double rw, double rh, PlaybackHdrColor hdr)
        {
            var lines = _renderLines;
            if (lines.Count == 0) return;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;

            int startIdx = Math.Max(0, _cachedVisibleStart - 3);
            int endIdx = Math.Min(lines.Count - 1, _cachedVisibleEnd + 3);
            if (UsesLineScroll && (_cachedVisibleStart < 0 || _cachedVisibleEnd < 0)) return;

            double lineOrigin = ry + rh * playingLineTopOffsetFactor;
            double yOffsetBase = lineOrigin + combinedScroll;
            double currentTimeMs = _internalTimeMs - _cachedOffsetMs;
            _lineRenderer.SetPlaybackColor(hdr, _cachedGlowAmount);

            if (_hoveredLineIndex >= startIdx && _hoveredLineIndex <= endIdx
                && lines[_hoveredLineIndex] is { } hovered && hovered.PrimaryTextLayout?.LayoutBounds.Width > 0)
            {
                double hoveredY = UsesLineScroll
                    ? lineOrigin + hovered.ScrollMotion.Value + _mouseYScrollTransition.Value
                    : yOffsetBase;
                if (HasTransformedLyrics)
                {
                    var originalTransform = ds.Transform;
                    try
                    {
                        ds.Transform = CreateLineTransform(hovered, rx, hoveredY, rw) * originalTransform;
                        DrawHoverBackground(sender, ds, hovered, 0, rw, 0, hdr);
                    }
                    finally
                    {
                        ds.Transform = originalTransform;
                    }
                }
                else
                {
                    DrawHoverBackground(sender, ds, hovered, rx, rw, hoveredY, hdr);
                }
            }

            for (int i = startIdx; i <= endIdx; i++)
            {
                var line = lines[i];
                if (line == null || line.PrimaryTextLayout == null) continue;
                if (line.PrimaryTextLayout.LayoutBounds.Width <= 0) continue;

                double yOffset = UsesLineScroll
                    ? lineOrigin + line.ScrollMotion.Value + _mouseYScrollTransition.Value
                    : yOffsetBase;

                bool isPlayingLine = line.GetIsHighlighted(currentTimeMs) || line.IsWordEffectsRetiring;

                line.EnsureCaches(sender, _cachedStrokeWidth);
                if (line.CachedFill == null) continue;
                if (line.UnplayedComposite == null) continue;

                if (line.UnplayedFillTint != null)
                    line.UnplayedFillTint.Color = _unplayedColor;
                if (line.UnplayedStrokeTint != null)
                    line.UnplayedStrokeTint.Color = _unplayedColor;

                var prevTransform = ds.Transform;
                // Render caches are recorded at Cached*Position. Height transitions
                // move the logical row without forcing per-frame Win2D cache rebuilds.
                var layoutOffset = Matrix3x2.CreateTranslation(0, (float)line.LayoutOffsetY);
                ds.Transform = layoutOffset * CreateLineTransform(line, rx, yOffset, rw) * prevTransform;

                _lineRenderer.IsPlaying = isPlayingLine;
                _lineRenderer.CurrentProgressMs = currentTimeMs;
                _lineRenderer.Line = line;
                _lineRenderer.PlayedFillColor = _playedColor;
                _lineRenderer.UnplayedFillColor = _unplayedColor;
                _lineRenderer.IsScaleEnabled = _cachedCharScaleAmount > 0;
                _lineRenderer.IsFloatEnabled = _cachedCharFloatAmount > 0;
                _lineRenderer.WordByWordEffectMode = _cachedWordByWordEffectMode;

                try
                {
                    _lineRenderer.Draw(sender, ds);
                }
                finally
                {
                    ds.Transform = prevTransform;
                }
            }
        }

        private void DrawHoverBackground(ICanvasResourceCreator sender, CanvasDrawContext ds, RenderLyricsLine line,
            double regionX, double regionWidth, double lineY, PlaybackHdrColor hdr)
        {
            // Brighten the surface at paper white and give light themes a visible edge near white.
            const byte hoverAlpha = 30;
            double left = Math.Max(regionX, regionX + line.TopLeftPosition.X - 20);
            double right = Math.Min(regionX + regionWidth, regionX + line.BottomRightPosition.X + 20);
            if (right <= left) return;
            var bounds = new Rect(left, lineY + line.TopLeftPosition.Y - 20, right - left,
                line.BottomRightPosition.Y - line.TopLeftPosition.Y + 40);

            if (hdr.IsEnabled)
            {
                var device = sender.Device;
                if (_hdrHoverBrush is null || !ReferenceEquals(_hdrHoverDevice, device))
                {
                    ReleaseHoverBrush();
                    _hdrHoverBrush = new CanvasSolidColorBrush(device, Colors.White);
                    _hdrHoverDevice = device;
                }
                if (_hdrHoverWhite != hdr.White)
                {
                    _hdrHoverBrush.ColorHdr = hdr.GetColor(Color.FromArgb(hoverAlpha, 255, 255, 255));
                    _hdrHoverWhite = hdr.White;
                }
                ds.FillRoundedRectangle(bounds, 6, 6, _hdrHoverBrush);
            }
            else
            {
                ds.FillRoundedRectangle(bounds, 6, 6, Color.FromArgb(hoverAlpha, 255, 255, 255));
            }
            if (!_isDarkTheme && bounds.Width > 1 && bounds.Height > 1)
            {
                var edge = new Rect(bounds.X + 0.5, bounds.Y + 0.5, bounds.Width - 1, bounds.Height - 1);
                ds.DrawRoundedRectangle(edge, 5.5f, 5.5f, Color.FromArgb(32, 0, 0, 0), 1f);
            }
        }

        private void ReleaseHoverBrush()
        {
            _hdrHoverBrush?.Dispose();
            _hdrHoverBrush = null;
            _hdrHoverDevice = null;
            _hdrHoverWhite = -1;
        }

        private void DisposeRenderLines()
        {
            DisposeLineList(_renderLines);
        }

        private static void DisposeLineList(IEnumerable<RenderLyricsLine> lines)
        {
            foreach (var line in lines)
            {
                line?.DisposeCaches();
                line?.DisposeTextLayout();
                line?.DisposeTextGeometry();
            }
        }

        private void DisposeRenderLineCaches()
        {
            foreach (var line in _renderLines)
                line?.DisposeCaches();
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public void OnSizeChanged()
        {
            _layoutDirty = true;
            InvalidateHost();
        }

        public void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            lock (_renderGate)
            {
                if (_shutdown) return;
                OnPointerWheelChangedCore(e);
            }
        }

        private void OnPointerWheelChangedCore(PointerRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;
            // 同一事件使用一个有作用域的原生快照，避免 PointerPoint/Properties 投影。
            using var point = PointerPointScope.GetCurrentPoint(e, InputElement);
            if (!IsPointerInLyricsRegion(point.Position)
                && (!HasTransformedLyrics || FindHoveredLyricsLine(point.Position, _smoothedScrollY + _mouseYScrollTransition.Value,
                    RegionH, _cachedPlayingLineTopOffset) < 0)) return;
            double delta = point.MouseWheelDelta * WheelScrollPixels * _cachedScrollSensitivity / 120.0;
            _pendingMouseScrollY += delta;
            _mouseYScrollTransition.Start(_pendingMouseScrollY);
            _isUserScrollingChanged = !_userScrolling;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            InvalidateHost();
            e.Handled = true;
        }

        public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (InputElement is null) return;
            using var point = PointerPointScope.GetCurrentPoint(e, InputElement);
            var pos = point.Position;
            _lastMousePos = pos;
            _hoverDirty = true;
            _isPointerOnSurface = true;

            e.Handled = true;
        }

        public void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOnSurface = false;
            _hoverDirty = true;
        }

        public void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerOnSurface = InputElement is not null;
            if (InputElement is { } canvas)
            {
                using var point = PointerPointScope.GetCurrentPoint(e, canvas);
                _lastMousePos = point.Position;
            }
            _hoverDirty = true;
        }

        public void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            TimeSpan? seekTime;
            lock (_renderGate)
            {
                if (_shutdown) return;
                seekTime = GetTappedTime(e);
            }
            // Host callbacks can trigger playback/UI work; never invoke them under the render lock.
            if (seekTime is { } time) LyricLineClicked?.Invoke(this, time);
        }

        private TimeSpan? GetTappedTime(TappedRoutedEventArgs e)
        {
            if (_renderLines.Count == 0 || _fadingOut || Volatile.Read(ref _pendingLines) is not null) return null;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;
            var tapPosition = e.GetPosition(InputElement);
            int hovered = FindHoveredLyricsLine(tapPosition, combinedScroll, RegionH, playingLineTopOffsetFactor);

            if (hovered >= 0 && hovered < _renderLines.Count)
            {
                var line = _renderLines[hovered];

                // 不再主动 JumpTo：让 host 收到事件后 seek，由 OnUpdate 里的
                // auto-scroll 把新播放行平滑拉到中央，避免双 source of truth。
                // 但仍要清掉用户滚动态：否则 OnUpdate 的 auto-scroll 守卫
                // (if (!_userScrolling && ...)) 会卡住直到 cooldown 过期。
                _mouseYScrollTransition.Start(0);
                _pendingMouseScrollY = 0;
                _userScrolling = false;
                _userScrollCooldownSec = 0;

                var time = line.StartMs + _cachedOffsetMs;
                if (time < 0) time = 0;
                return TimeSpan.FromMilliseconds(time);
            }
            return null;
        }

        internal void RaiseRenderError(Exception ex) => RenderError?.Invoke(this, ex);
    }
}
