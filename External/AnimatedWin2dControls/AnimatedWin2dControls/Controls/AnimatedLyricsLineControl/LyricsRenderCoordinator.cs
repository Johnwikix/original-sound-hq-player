using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    /// <summary>
    /// 高级歌词渲染协调器。承载原 <see cref="AdvanceLyricsCanvasControl"/> 的渲染/输入/滚动过渡/Bus 逻辑，
    /// 面向外部传入的 <see cref="CanvasAnimatedControl"/> 与 <see cref="CanvasDrawingSession"/> 工作。
    /// 既被 AdvanceLyricsCanvasControl（独立场景）使用，也被合成宿主 NowPlayingCanvas（单 canvas）使用。
    /// </summary>
    public sealed class LyricsRenderCoordinator
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;
        public event EventHandler<Exception>? RenderError;

        /// <summary>宿主设置的目标画布。用于 Invalidate / Size / 指针捕获 / DPI 换算 / 帧率设置。</summary>
        public CanvasAnimatedControl? Canvas { get; set; }

        /// <summary>
        /// 歌词渲染区域（相对画布的矩形）。当 Width/Height &lt;= 0 时退化为整块画布，
        /// 用于独立 AdvanceLyricsCanvasControl（其画布本身已被布局到歌词单元格）。
        /// 合成宿主（全屏画布）通过设置此区域把歌词限制在右栏。
        /// </summary>
        public Rect LyricsRegion { get; set; }

        private double RegionX => LyricsRegion.Width > 0 ? LyricsRegion.X : 0;
        private double RegionY => LyricsRegion.Height > 0 ? LyricsRegion.Y : 0;
        private double RegionW => LyricsRegion.Width > 0 ? LyricsRegion.Width : (Canvas?.Size.Width ?? 0);
        private double RegionH => LyricsRegion.Height > 0 ? LyricsRegion.Height : (Canvas?.Size.Height ?? 0);

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
        private List<RenderLyricsLine>? _pendingDisposeLines;
        private bool _layoutDirty = true;
        private bool _shutdown;
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
        private bool _userScrolling;
        private bool _isUserScrollingChanged;
        private double _userScrollCooldownSec;
        private const double UserScrollCooldown = 2.5;

        private double _internalTimeMs;
        private double _lastExternalTimeMs;
        private const double SyncThresholdMs = 250.0;

        public double AutoScrollSpeed = 4.0;
        public double UserScrollReturnSpeed = 12.0;
        public double WheelScrollPixels = 80.0;

        private int _hoveredLineIndex = -1;
        private bool _isMouseInLyricsArea;
        private Point _lastMousePos;
        private bool _hoverDirty;

        private int _cachedVisibleStart;
        private int _cachedVisibleEnd;
        private double _cachedEdgeFadeWidth;
        private double _cachedEdgeFadeHeight;

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
        private double _cachedStrokeWidth;
        private EasingType _cachedScrollEasingType = EasingType.Sine;
        private EaseMode _cachedScrollEasingMode = EaseMode.Out;
        private double _cachedPlayingLineTopOffset = 0.35;
        private double _cachedTargetFrameRate = 60.0;

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
            // 先设初始默认色，再订阅并请求同步；Request 会同步回灌真实 isDark，
            // 必须早于此默认，避免把正确颜色覆盖回浅色默认（导致暗色模式显示黑字）。
            OnIsDarkChanged(false);

            TimeProgressBus.CurrentPlayingTimeChanged += OnCurrentPlayingTimeChanged;
            IsPlayingBus.Changed += OnIsPlayingChanged;
            OffsetMsBus.Changed += OnOffsetMsChanged;
            LyricsFontSizeBus.Changed += OnLyricsFontSizeChanged;
            UILyricsBus.Changed += OnUILyricsChangedBus;
            LyricsSettingsBus.SyncRequested += OnLyricsSettingsChanged;
            LyricsSyncRequestBus.Request();
        }

        public void Detach()
        {
            TimeProgressBus.CurrentPlayingTimeChanged -= OnCurrentPlayingTimeChanged;
            IsPlayingBus.Changed -= OnIsPlayingChanged;
            OffsetMsBus.Changed -= OnOffsetMsChanged;
            LyricsFontSizeBus.Changed -= OnLyricsFontSizeChanged;
            UILyricsBus.Changed -= OnUILyricsChangedBus;
            LyricsSettingsBus.SyncRequested -= OnLyricsSettingsChanged;
        }

        public void ResetTimeSync() => _lastExternalTimeMs = 0;

        public void OnCreateResources()
        {
            if (_shutdown) return;
            _layoutDirty = true;
            _cachedEdgeFadeWidth = -1;
            _cachedEdgeFadeHeight = -1;
        }

        public void PrepareForShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;
            Detach();
            DisposeRenderLines();
            if (_pendingDisposeLines != null)
            {
                foreach (var line in _pendingDisposeLines)
                    line?.DisposeTextLayout();
                _pendingDisposeLines = null;
            }
            _edgeFadeMask.Dispose();
            Canvas = null;
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
            Canvas?.Invalidate();
        }

        private void OnUILyricsChangedBus(IList<LyricLine>? value) => OnUILyricsChanged(value);

        private void OnLyricsSettingsChanged(LyricsSettingsBus.Settings s)
        {
            _cachedFontFamilyName = s.FontFamilyName;
            _cachedLyricsTextAlignment = s.LyricsTextAlignment;
            _cachedScrollSensitivity = s.ScrollSensitivity;
            _cachedLyricsBlurAmount = s.LyricsBlurAmount;
            _cachedGlowAmount = s.GlowAmount;
            _cachedCharFloatAmount = s.CharFloatAmount;
            _cachedCharScaleAmount = s.CharScaleAmount;
            _cachedLongSyllableThreshold = s.LongSyllableThreshold;
            _cachedIsFadeOutEnabled = s.IsFadeOutEnabled;
            _cachedIsOutOfSightEnabled = s.IsOutOfSightEnabled;
            _cachedUnplayedOpacity = s.UnplayedOpacity;
            _cachedTranslatedOpacity = s.TranslatedOpacity;
            _cachedStrokeWidth = s.StrokeWidth;
            _cachedScrollEasingType = s.ScrollEasingType;
            _cachedScrollEasingMode = s.ScrollEasingMode;
            _cachedPlayingLineTopOffset = s.PlayingLineTopOffset;
            _cachedTargetFrameRate = s.TargetFrameRate;
            if (Canvas is not null)
                Canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / s.TargetFrameRate);

            bool isDark = s.IsDark;
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

            _layoutDirty = true;
            Canvas?.Invalidate();
        }

        private void OnUILyricsChanged(IList<LyricLine>? newLyrics)
        {
            _pendingDisposeLines = _renderLines;

            var newLines = new List<RenderLyricsLine>();
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

            _renderLines = newLines;
            _layoutDirty = true;
            Canvas?.Invalidate();
        }

        private void OnIsDarkChanged(bool isDark)
        {
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
            Canvas?.Invalidate();
        }

        // ── 布局 ──────────────────────────────────────────────────────────────

        private double _lastLayoutWidth;
        private double _lastLayoutHeight;

        private void EnsureLayout(ICanvasResourceCreator resourceCreator)
        {
            if (_pendingDisposeLines != null)
            {
                foreach (var line in _pendingDisposeLines)
                {
                    line?.DisposeCaches();
                    line?.DisposeTextLayout();
                    line?.DisposeTextGeometry();
                }
                _pendingDisposeLines = null;
            }

            if (Canvas == null || _renderLines.Count == 0) return;
            var layoutLines = _renderLines;

            double regionW = RegionW;
            double regionH = RegionH;

            if (!_layoutDirty && Math.Abs(regionW - _lastLayoutWidth) < 0.5 && Math.Abs(regionH - _lastLayoutHeight) < 0.5)
                return;

            _lastLayoutWidth = regionW;
            _lastLayoutHeight = regionH;

            int originalFontSize = (int)_cachedLyricsFontSize;
            int translatedFontSize = (int)(_cachedLyricsFontSize * 0.7);

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
                    0);

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
        {
            EnsureLayout(sender);
            var lines = _renderLines;
            if (lines.Count == 0) return;

            // 单调自增，每帧一格。LyricsAnimator 内部用 animationVersion - 1
            // 来识别"上一帧不在动画范围内"的行，强制重算距离效果。
            _animationVersion++;

            double externalTimeMs = _cachedCurrentPlayingTimeMs;

            bool isPrimaryPlayingLineChanged = false;
            double currentTimeMs = 0;

            if (_cachedIsPlaying)
            {
                if (Math.Abs(externalTimeMs - _lastExternalTimeMs) > SyncThresholdMs)
                {
                    _internalTimeMs = externalTimeMs;
                }
                else
                {
                    _internalTimeMs += args.Timing.ElapsedTime.TotalMilliseconds;
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
                        EasingHelper.GetInterpolatorByEasingType<double>(_cachedScrollEasingType, _cachedScrollEasingMode));
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

            double dt = args.Timing.ElapsedTime.TotalSeconds;

            if (!_userScrolling && _cachedIsPlaying && _currentLineIndex >= 0 && _currentLineIndex < lines.Count)
            {
                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(lines, _currentLineIndex);
                if (targetScroll.HasValue)
                {
                    _canvasYScrollTransition.SetDurationMs(300);
                    if (_layoutDirty)
                    {
                        _canvasYScrollTransition.JumpTo(targetScroll.Value);
                        _targetScrollY = targetScroll.Value;
                        _smoothedScrollY = targetScroll.Value;
                    }
                    else
                    {
                        _canvasYScrollTransition.Start(targetScroll.Value);
                    }
                }
            }

            _canvasYScrollTransition.Update(args.Timing.ElapsedTime);
            _mouseYScrollTransition.Update(args.Timing.ElapsedTime);

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

            var visibleRange = LyricsLayoutManager.CalculateVisibleRange(
                lines, combinedScroll, 0, canvasHeight, canvasHeight, playingLineTopOffsetFactor);

            _cachedVisibleStart = visibleRange.Start;
            _cachedVisibleEnd = visibleRange.End;

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
                args.Timing.ElapsedTime,
                _userScrolling,
                _isUserScrollingChanged,
                _layoutDirty,
                isPrimaryPlayingLineChanged,
                currentTimeMs,
                _animationVersion);

            _layoutDirty = false;
            _isUserScrollingChanged = false;

            HandleHoverUpdates(combinedScroll, canvasHeight, playingLineTopOffsetFactor);
        }

        private void HandleHoverUpdates(double combinedScroll, double canvasHeight, double playingLineTopOffsetFactor)
        {
            if (!_hoverDirty) return;
            _hoverDirty = false;

            var lines = _renderLines;
            if (lines.Count == 0) return;

            if (_isMouseInLyricsArea)
            {
                var regionLocalMouse = new Point(_lastMousePos.X - RegionX, _lastMousePos.Y - RegionY);
                var newHovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                    lines, true, regionLocalMouse, combinedScroll,
                    canvasHeight, playingLineTopOffsetFactor);
                _hoveredLineIndex = newHovered;
            }
            else
            {
                _hoveredLineIndex = -1;
            }
        }

        private static readonly CanvasGradientStop[] s_edgeFadeStops = new CanvasGradientStop[]
        {
            new() { Position = 0.00f, Color = Colors.Transparent },
            new() { Position = 0.05f, Color = Colors.White },
            new() { Position = 0.35f, Color = Colors.White },
            new() { Position = 0.65f, Color = Colors.White },
            new() { Position = 0.95f, Color = Colors.White },
            new() { Position = 1.00f, Color = Colors.Transparent },
        };

        // ── Draw（不负责清屏，由宿主决定清屏色） ─────────────────────────────────

        public void OnDraw(ICanvasAnimatedControl sender, CanvasDrawingSession ds)
        {
            if (_renderLines.Count == 0) return;

            double rx = RegionX;
            double ry = RegionY;
            double rw = RegionW;
            double rh = RegionH;
            if (rw <= 0 || rh <= 0) return;

            if (Math.Abs(_cachedEdgeFadeWidth - rw) > 0.5f || Math.Abs(_cachedEdgeFadeHeight - rh) > 0.5f)
            {
                _cachedEdgeFadeWidth = rw;
                _cachedEdgeFadeHeight = rh;
                _edgeFadeMask.Update(sender, new Rect(rx, ry, rw, rh), s_edgeFadeStops, true);
            }

            if (_edgeFadeMask.Brush != null)
            {
                using (ds.CreateLayer(_edgeFadeMask.Brush))
                {
                    DrawLyricsContent(sender, ds, rx, ry, rw, rh);
                }
            }
            else
            {
                DrawLyricsContent(sender, ds, rx, ry, rw, rh);
            }
        }

        private void DrawLyricsContent(ICanvasAnimatedControl sender, CanvasDrawingSession ds, double rx, double ry, double rw, double rh)
        {
            var lines = _renderLines;
            if (lines.Count == 0) return;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;

            int startIdx = Math.Max(0, _cachedVisibleStart - 3);
            int endIdx = Math.Min(lines.Count - 1, _cachedVisibleEnd + 3);

            double yOffsetBase = ry + rh * playingLineTopOffsetFactor + combinedScroll;
            double currentTimeMs = _internalTimeMs - _cachedOffsetMs;

            for (int i = startIdx; i <= endIdx; i++)
            {
                var line = lines[i];
                if (line == null || line.PrimaryTextLayout == null) continue;
                if (line.PrimaryTextLayout.LayoutBounds.Width <= 0) continue;

                double yOffset = yOffsetBase;

                bool isPlayingLine = line.GetIsPlaying(currentTimeMs);

                line.EnsureCaches(sender, _cachedStrokeWidth);
                if (line.CachedFill == null) continue;
                if (line.UnplayedComposite == null) continue;

                if (line.UnplayedFillTint != null)
                    line.UnplayedFillTint.Color = _unplayedColor;
                if (line.UnplayedStrokeTint != null)
                    line.UnplayedStrokeTint.Color = _unplayedColor;

                var prevTransform = ds.Transform;
                ds.Transform *= Matrix3x2.CreateScale((float)line.ScaleTransition.Value, line.CenterPosition);
                ds.Transform *= Matrix3x2.CreateTranslation((float)rx, (float)yOffset);

                _lineRenderer.IsPlaying = isPlayingLine;
                _lineRenderer.CurrentProgressMs = currentTimeMs;
                _lineRenderer.Line = line;
                _lineRenderer.PlayedFillColor = _playedColor;
                _lineRenderer.UnplayedFillColor = _unplayedColor;
                _lineRenderer.IsGlowEnabled = _cachedGlowAmount > 0;
                _lineRenderer.IsScaleEnabled = _cachedCharScaleAmount > 0;
                _lineRenderer.IsFloatEnabled = _cachedCharFloatAmount > 0;

                _lineRenderer.Draw(sender, ds);
                ds.Transform = prevTransform;

                if (i == _hoveredLineIndex)
                {
                    var hoverRect = new Rect(
                        rx + line.TopLeftPosition.X - 20,
                        yOffset + line.TopLeftPosition.Y - 20,
                        line.BottomRightPosition.X - line.TopLeftPosition.X + 40,
                        line.BottomRightPosition.Y - line.TopLeftPosition.Y + 40);

                    double rightBound = rx + rw;
                    if (hoverRect.X < rx) { hoverRect.Width += hoverRect.X - rx; hoverRect.X = rx; }
                    if (hoverRect.X + hoverRect.Width > rightBound) hoverRect.Width = rightBound - hoverRect.X;
                    if (hoverRect.Width <= 0) continue;

                    ds.FillRoundedRectangle(hoverRect, 6, 6,
                        Color.FromArgb(30, 255, 255, 255));
                }
            }
        }

        private void DisposeRenderLines()
        {
            foreach (var line in _renderLines)
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
            Canvas?.Invalidate();
        }

        public void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;
            if (!IsPointerInLyricsRegion(e.GetCurrentPoint(Canvas).Position)) return;
            var props = e.GetCurrentPoint(Canvas).Properties;
            double delta = props.MouseWheelDelta * WheelScrollPixels * _cachedScrollSensitivity / 120.0;
            _pendingMouseScrollY += delta;
            _mouseYScrollTransition.Start(_pendingMouseScrollY);
            _isUserScrollingChanged = !_userScrolling;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            Canvas?.Invalidate();
            e.Handled = true;
        }

        public void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (Canvas == null) return;
            var pos = e.GetCurrentPoint(Canvas).Position;
            _lastMousePos = pos;
            _hoverDirty = true;
            // 持续追踪：_isMouseInLyricsArea 跟随当前指针位置更新，不依赖
            // OnPointerEntered/Exited 的一次性信号（画布内的"背景↔歌词"互转不发 enter/exit）。
            _isMouseInLyricsArea = IsPointerInLyricsRegion(pos);

            e.Handled = true;
        }

        public void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isMouseInLyricsArea = false;
            _hoverDirty = true;
        }

        public void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            // 兜底：刚跨进画布时 _isMouseInLyricsArea 还没被 OnPointerMoved 刷新过
            // （跨进画布后的第一帧 OnPointerMoved 在 Entered 之后才到）。真正的持续追踪
            // 在 OnPointerMoved 里。
            _isMouseInLyricsArea = Canvas != null && IsPointerInLyricsRegion(e.GetCurrentPoint(Canvas).Position);
            _hoverDirty = true;
        }

        public void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;
            var regionLocalMouse = new Point(_lastMousePos.X - RegionX, _lastMousePos.Y - RegionY);
            int hovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                _renderLines, true, regionLocalMouse, combinedScroll,
                RegionH > 0 ? RegionH : (Canvas?.Size.Height ?? 400), playingLineTopOffsetFactor);

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
                LyricLineClicked?.Invoke(this, TimeSpan.FromMilliseconds(time));
            }
        }

        internal void RaiseRenderError(Exception ex) => RenderError?.Invoke(this, ex);
    }
}
