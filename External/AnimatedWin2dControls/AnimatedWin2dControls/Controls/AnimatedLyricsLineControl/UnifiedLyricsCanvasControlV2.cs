using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public sealed class UnifiedLyricsCanvasControlV2 : Control
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;

        private CanvasAnimatedControl? _canvas;
        private List<RenderLyricsLine> _renderLines = [];
        private bool _layoutDirty = true;
        private int _currentLineIndex = -1;
        private int _lastCurrentLineIndex = -1;

        private LyricsSynchronizer _synchronizer = new();
        private LyricsAnimator _animator = new();
        private LyricsLineRendererV2 _lineRenderer = new();
        private EdgeFadeMaskRenderer _edgeFadeMask = new();

        private ValueTransition<double> _canvasYScrollTransition;

        private double _targetScrollY;
        private double _smoothedScrollY;
        private bool _userScrolling;
        private double _userScrollCooldownSec;
        private const double UserScrollCooldown = 2.5;

        public double AutoScrollSpeed = 4.0;
        public double UserScrollReturnSpeed = 12.0;
        public double WheelScrollPixels = 80.0;

        private bool _pointerCaptured;
        private double _pointerLastY;
        private double _flingY;
        private const double FlingDecay = 0.92;
        private const double FlingStopThreshold = 5.0;

        private int _hoveredLineIndex = -1;
        private int _previousHoveredLineIndex = -1;
        private bool _isMouseInLyricsArea;
        private Point _lastMousePos;

        private Color _playedColor = Colors.White;
        private Color _unplayedColor = Color.FromArgb(80, 255, 255, 255);

        // ── 渲染线程安全缓存 ────────────────────────────────────────────────
        private double _cachedLyricsFontSize = 36.0;
        private string _cachedFontFamilyName = "Segoe UI";
        private CanvasHorizontalAlignment _cachedLyricsTextAlignment = CanvasHorizontalAlignment.Left;
        private TimeSpan _cachedCurrentPlayingTime = TimeSpan.Zero;
        private double _cachedOffsetMs;
        private bool _cachedIsPlaying;
        private double _cachedLyricsBlurAmount = 4.0;
        private double _cachedGlowAmount;
        private double _cachedCharFloatAmount;
        private double _cachedCharScaleAmount;
        private double _cachedLongSyllableThreshold = 500.0;
        private double _cachedScrollSensitivity = 1.0;

        public UnifiedLyricsCanvasControlV2()
        {
            DefaultStyleKey = typeof(UnifiedLyricsCanvasControlV2);
            SizeChanged += OnControlSizeChanged;
            Unloaded += OnControlUnloaded;

            _canvasYScrollTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            DisposeRenderLines();
            _edgeFadeMask.Dispose();
        }

        protected override void OnApplyTemplate()
        {
            if (_canvas != null)
            {
                _canvas.Update -= OnCanvasUpdate;
                _canvas.Draw -= OnCanvasDraw;
                _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
                _canvas.PointerPressed -= OnCanvasPointerPressed;
                _canvas.PointerMoved -= OnCanvasPointerMoved;
                _canvas.PointerReleased -= OnCanvasPointerReleased;
                _canvas.PointerCanceled -= OnCanvasPointerCanceled;
                _canvas.PointerExited -= OnCanvasPointerExited;
                _canvas.PointerEntered -= OnCanvasPointerEntered;
                _canvas.Tapped -= OnCanvasTapped;
            }

            _canvas = GetTemplateChild("PART_Canvas") as CanvasAnimatedControl;

            if (_canvas != null)
            {
                _canvas.Update += OnCanvasUpdate;
                _canvas.Draw += OnCanvasDraw;
                _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
                _canvas.PointerPressed += OnCanvasPointerPressed;
                _canvas.PointerMoved += OnCanvasPointerMoved;
                _canvas.PointerReleased += OnCanvasPointerReleased;
                _canvas.PointerCanceled += OnCanvasPointerCanceled;
                _canvas.PointerExited += OnCanvasPointerExited;
                _canvas.PointerEntered += OnCanvasPointerEntered;
                _canvas.Tapped += OnCanvasTapped;
            }
        }

        #region Dependency Properties

        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(nameof(UILyrics), typeof(IList<LyricLine>), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(null, (d, e) => ((UnifiedLyricsCanvasControlV2)d).OnUILyricsChanged(e.NewValue as IList<LyricLine>)));

        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(nameof(CurrentPlayingTime), typeof(TimeSpan), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(TimeSpan.Zero, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedCurrentPlayingTime = (TimeSpan)e.NewValue));

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(false, (d, e) => { var c = (UnifiedLyricsCanvasControlV2)d; c._cachedIsPlaying = (bool)e.NewValue; c._matchLyricLineNextFrame = true; }));

        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(nameof(LyricsFontSize), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(36.0, (d, e) => { var c = (UnifiedLyricsCanvasControlV2)d; c._cachedLyricsFontSize = (double)e.NewValue; c._layoutDirty = true; }));

        public static readonly DependencyProperty FontFamilyNameProperty =
            DependencyProperty.Register(nameof(FontFamilyName), typeof(string), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata("Segoe UI", (d, e) => { var c = (UnifiedLyricsCanvasControlV2)d; c._cachedFontFamilyName = (string)e.NewValue; c._layoutDirty = true; }));

        public static readonly DependencyProperty LyricsTextAlignmentProperty =
            DependencyProperty.Register(nameof(LyricsTextAlignment), typeof(CanvasHorizontalAlignment), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(CanvasHorizontalAlignment.Left, (d, e) => { var c = (UnifiedLyricsCanvasControlV2)d; c._cachedLyricsTextAlignment = (CanvasHorizontalAlignment)e.NewValue; c._layoutDirty = true; }));

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(false, (d, e) => ((UnifiedLyricsCanvasControlV2)d).OnIsDarkChanged()));

        public static readonly DependencyProperty OffsetMsProperty =
            DependencyProperty.Register(nameof(OffsetMs), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(0.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedOffsetMs = (double)e.NewValue));

        public static readonly DependencyProperty ScrollSensitivityProperty =
            DependencyProperty.Register(nameof(ScrollSensitivity), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(1.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedScrollSensitivity = (double)e.NewValue));

        public static readonly DependencyProperty LyricsBlurAmountProperty =
            DependencyProperty.Register(nameof(LyricsBlurAmount), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(4.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedLyricsBlurAmount = (double)e.NewValue));

        public static readonly DependencyProperty GlowAmountProperty =
            DependencyProperty.Register(nameof(GlowAmount), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(0.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedGlowAmount = (double)e.NewValue));

        public static readonly DependencyProperty CharFloatAmountProperty =
            DependencyProperty.Register(nameof(CharFloatAmount), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(0.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedCharFloatAmount = (double)e.NewValue));

        public static readonly DependencyProperty CharScaleAmountProperty =
            DependencyProperty.Register(nameof(CharScaleAmount), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(0.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedCharScaleAmount = (double)e.NewValue));

        public static readonly DependencyProperty LongSyllableThresholdProperty =
            DependencyProperty.Register(nameof(LongSyllableThreshold), typeof(double), typeof(UnifiedLyricsCanvasControlV2),
                new PropertyMetadata(500.0, (d, e) => ((UnifiedLyricsCanvasControlV2)d)._cachedLongSyllableThreshold = (double)e.NewValue));

        public IList<LyricLine>? UILyrics
        {
            get => (IList<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

        public TimeSpan CurrentPlayingTime
        {
            get => _cachedCurrentPlayingTime;
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        public bool IsPlaying
        {
            get => _cachedIsPlaying;
            set => SetValue(IsPlayingProperty, value);
        }

        public double LyricsFontSize
        {
            get => _cachedLyricsFontSize;
            set => SetValue(LyricsFontSizeProperty, value);
        }

        public string FontFamilyName
        {
            get => _cachedFontFamilyName;
            set => SetValue(FontFamilyNameProperty, value);
        }

        public CanvasHorizontalAlignment LyricsTextAlignment
        {
            get => _cachedLyricsTextAlignment;
            set => SetValue(LyricsTextAlignmentProperty, value);
        }

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        public double OffsetMs
        {
            get => _cachedOffsetMs;
            set => SetValue(OffsetMsProperty, value);
        }

        public double ScrollSensitivity
        {
            get => _cachedScrollSensitivity;
            set => SetValue(ScrollSensitivityProperty, value);
        }

        public double LyricsBlurAmount
        {
            get => _cachedLyricsBlurAmount;
            set => SetValue(LyricsBlurAmountProperty, value);
        }

        public double GlowAmount
        {
            get => _cachedGlowAmount;
            set => SetValue(GlowAmountProperty, value);
        }

        public double CharFloatAmount
        {
            get => _cachedCharFloatAmount;
            set => SetValue(CharFloatAmountProperty, value);
        }

        public double CharScaleAmount
        {
            get => _cachedCharScaleAmount;
            set => SetValue(CharScaleAmountProperty, value);
        }

        public double LongSyllableThreshold
        {
            get => _cachedLongSyllableThreshold;
            set => SetValue(LongSyllableThresholdProperty, value);
        }

        #endregion

        private bool _matchLyricLineNextFrame;

        private void OnUILyricsChanged(IList<LyricLine>? newLyrics)
        {
            DisposeRenderLines();
            _renderLines.Clear();

            if (newLyrics != null && newLyrics.Count > 0)
            {
                for (int i = 0; i < newLyrics.Count; i++)
                {
                    var lyricLine = newLyrics[i];
                    double nextStartMs = (i + 1 < newLyrics.Count)
                        ? newLyrics[i + 1].Time.TotalMilliseconds
                        : lyricLine.Time.TotalMilliseconds + 10000;

                    var renderLine = new RenderLyricsLine();
                    renderLine.LoadFromLyricLine(lyricLine, nextStartMs);
                    _renderLines.Add(renderLine);
                }
            }

            _layoutDirty = true;
            _synchronizer.Reset();
            _currentLineIndex = -1;
            _lastCurrentLineIndex = -1;
            _matchLyricLineNextFrame = true;
            _canvas?.Invalidate();
        }

        private void OnIsDarkChanged()
        {
            // IsDark is accessed from UI thread only in this callback, safe via GetValue
            bool isDark = (bool)GetValue(IsDarkProperty);
            if (isDark)
            {
                _playedColor = Colors.White;
                _unplayedColor = Color.FromArgb(80, 255, 255, 255);
            }
            else
            {
                _playedColor = Color.FromArgb(255, 0, 0, 0);
                _unplayedColor = Color.FromArgb(80, 0, 0, 0);
            }

            DisposeRenderLineCaches();
            _layoutDirty = true;
            _canvas?.Invalidate();
        }

        private double _lastLayoutWidth;
        private double _lastLayoutHeight;

        private void EnsureLayout(ICanvasResourceCreator resourceCreator)
        {
            if (_canvas == null || _renderLines.Count == 0) return;

            if (!_layoutDirty && Math.Abs(_canvas.Size.Width - _lastLayoutWidth) < 0.5 && Math.Abs(_canvas.Size.Height - _lastLayoutHeight) < 0.5)
                return;

            _layoutDirty = false;
            _lastLayoutWidth = _canvas.Size.Width;
            _lastLayoutHeight = _canvas.Size.Height;

            int originalFontSize = (int)_cachedLyricsFontSize;
            int translatedFontSize = (int)(_cachedLyricsFontSize * 0.7);

            try
            {
                LyricsLayoutManager.MeasureAndArrange(
                    resourceCreator,
                    _renderLines,
                    originalFontSize,
                    translatedFontSize,
                    _cachedFontFamilyName,
                    _cachedLyricsTextAlignment,
                    _canvas.Size.Width,
                    _canvas.Size.Height,
                    0);

                if (_currentLineIndex >= 0)
                {
                    var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLines, _currentLineIndex);
                    if (targetScroll.HasValue)
                    {
                        _canvasYScrollTransition.JumpTo(targetScroll.Value);
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

        private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            EnsureLayout(sender);

            if (_renderLines.Count == 0) return;

            double currentTimeMs = _cachedCurrentPlayingTime.TotalMilliseconds + _cachedOffsetMs;

            if (!_cachedIsPlaying)
            {
                _matchLyricLineNextFrame = true;
                return;
            }

            if (_matchLyricLineNextFrame)
            {
                _matchLyricLineNextFrame = false;
                int newIndex = _synchronizer.GetCurrentLineIndex(currentTimeMs, _renderLines);
                _lastCurrentLineIndex = _currentLineIndex;
                if (newIndex != _currentLineIndex)
                    _currentLineIndex = newIndex;
            }
            else
            {
                int newIndex = _synchronizer.GetCurrentLineIndex(currentTimeMs, _renderLines);
                _lastCurrentLineIndex = _currentLineIndex;
                _currentLineIndex = newIndex;
            }

            bool isPrimaryPlayingLineChanged = _lastCurrentLineIndex != _currentLineIndex;

            double dt = args.Timing.ElapsedTime.TotalSeconds;

            if (!_userScrolling && _currentLineIndex >= 0 && _currentLineIndex < _renderLines.Count)
            {
                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLines, _currentLineIndex);
                if (targetScroll.HasValue)
                    _targetScrollY = targetScroll.Value;
            }

            if (_userScrollCooldownSec > 0)
                _userScrollCooldownSec -= dt;

            if (_userScrolling && _userScrollCooldownSec <= 0 && !_pointerCaptured)
                _userScrolling = false;

            if (_flingY != 0)
            {
                _targetScrollY += _flingY * dt;
                _flingY *= 1.0 - (1.0 - FlingDecay) * dt * 60.0;
                if (Math.Abs(_flingY) < FlingStopThreshold) _flingY = 0;
            }

            double scrollLerp = _userScrolling ? UserScrollReturnSpeed : AutoScrollSpeed;
            _smoothedScrollY += (_targetScrollY - _smoothedScrollY) * (1.0 - Math.Exp(-scrollLerp * dt));

            _canvasYScrollTransition.Update(args.Timing.ElapsedTime);

            double canvasHeight = _canvas?.Size.Height ?? 400;
            double playingLineTopOffsetFactor = 0.35;

            HandleHoverUpdates(canvasHeight, playingLineTopOffsetFactor);

            double visibleScrollY = _userScrolling ? _targetScrollY :
                Math.Min(_smoothedScrollY, _targetScrollY);

            var visibleRange = LyricsLayoutManager.CalculateVisibleRange(
                _renderLines, visibleScrollY, 0, canvasHeight, canvasHeight, playingLineTopOffsetFactor);

            for (int i = Math.Max(0, visibleRange.Start); i <= Math.Min(_renderLines.Count - 1, visibleRange.End + 1); i++)
            {
                _renderLines[i].YOffsetTransition.JumpTo(_smoothedScrollY);
            }

            _animator.UpdateLines(
                _renderLines,
                visibleRange.Start,
                visibleRange.End,
                _currentLineIndex,
                canvasHeight,
                _smoothedScrollY,
                playingLineTopOffsetFactor,
                _cachedLyricsBlurAmount > 0,
                _cachedLyricsBlurAmount > 0,
                _cachedGlowAmount > 0,
                _cachedLongSyllableThreshold,
                _cachedGlowAmount,
                _cachedCharFloatAmount > 0,
                450,
                _cachedCharFloatAmount,
                _cachedCharScaleAmount > 0,
                _cachedLongSyllableThreshold,
                _cachedCharScaleAmount / 100.0,
                _cachedLyricsBlurAmount,
                0.3,
                1.0,
                0.6,
                _canvasYScrollTransition,
                args.Timing.ElapsedTime,
                _userScrolling,
                _layoutDirty,
                isPrimaryPlayingLineChanged,
                currentTimeMs);
        }

        private void HandleHoverUpdates(double canvasHeight, double playingLineTopOffsetFactor)
        {
            if (_renderLines.Count == 0) return;

            var oldHovered = _previousHoveredLineIndex;
            _previousHoveredLineIndex = _hoveredLineIndex;

            if (_isMouseInLyricsArea)
            {
                var newHovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                    _renderLines, true, _lastMousePos, _smoothedScrollY,
                    canvasHeight, playingLineTopOffsetFactor);
                _hoveredLineIndex = newHovered;
            }
            else
            {
                _hoveredLineIndex = -1;
            }

            if (_hoveredLineIndex != oldHovered)
            {
                if (oldHovered >= 0 && oldHovered < _renderLines.Count)
                {
                    var oldLine = _renderLines[oldHovered];
                    double oldBlur = _cachedLyricsBlurAmount > 0 ? _cachedLyricsBlurAmount * 0.5 : 0;
                    oldLine.BlurAmountTransition.SetDuration(0.5);
                    oldLine.BlurAmountTransition.Start(oldBlur);
                }

                if (_hoveredLineIndex >= 0 && _hoveredLineIndex < _renderLines.Count)
                {
                    var newLine = _renderLines[_hoveredLineIndex];
                    newLine.BlurAmountTransition.SetDuration(0.2);
                    newLine.BlurAmountTransition.Start(0);
                }
            }
        }

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            ds.Clear(Microsoft.UI.Colors.Transparent);

            if (_renderLines.Count == 0) return;

            double canvasHeight = (float)sender.Size.Height;
            double canvasWidth = (float)sender.Size.Width;

            var stops = new CanvasGradientStop[]
            {
                new() { Position = 0.00f, Color = Microsoft.UI.Colors.Transparent },
                new() { Position = 0.05f, Color = Microsoft.UI.Colors.White },
                new() { Position = 0.35f, Color = Microsoft.UI.Colors.White },
                new() { Position = 0.65f, Color = Microsoft.UI.Colors.White },
                new() { Position = 0.95f, Color = Microsoft.UI.Colors.White },
                new() { Position = 1.00f, Color = Microsoft.UI.Colors.Transparent },
            };

            _edgeFadeMask.Update(sender, new Rect(0, 0, canvasWidth, canvasHeight), stops, true);

            if (_edgeFadeMask.Brush != null)
            {
                using (ds.CreateLayer(_edgeFadeMask.Brush))
                {
                    DrawLyricsContent(sender, ds, canvasHeight);
                }
            }
            else
            {
                DrawLyricsContent(sender, ds, canvasHeight);
            }
        }

        private void DrawLyricsContent(ICanvasAnimatedControl sender, CanvasDrawingSession ds, double canvasHeight)
        {
            double playingLineTopOffsetFactor = 0.35;
            double drawScrollY = _userScrolling ? _targetScrollY :
                Math.Min(_smoothedScrollY, _targetScrollY);

            var visibleRange = LyricsLayoutManager.CalculateVisibleRange(
                _renderLines, drawScrollY, 0, canvasHeight, canvasHeight, playingLineTopOffsetFactor);

            int startIdx = Math.Max(0, visibleRange.Start);
            int endIdx = Math.Min(_renderLines.Count - 1, visibleRange.End);

            double yOffsetBase = canvasHeight * playingLineTopOffsetFactor;
            double currentTimeMs = _cachedCurrentPlayingTime.TotalMilliseconds + _cachedOffsetMs;

            for (int i = startIdx; i <= endIdx; i++)
            {
                var line = _renderLines[i];
                if (line == null || line.PrimaryTextLayout == null) continue;
                if (line.PrimaryTextLayout.LayoutBounds.Width <= 0) continue;

                double yOffset = line.YOffsetTransition.Value + yOffsetBase;

                bool isPlayingLine = line.GetIsPlaying(currentTimeMs);

                line.EnsureCaches(sender, 0);
                if (line.CachedStroke == null && line.CachedFill == null) continue;
                if (line.UnplayedComposite == null) continue;

                var prevTransform = ds.Transform;
                ds.Transform *= Matrix3x2.CreateScale((float)line.ScaleTransition.Value, line.CenterPosition);
                ds.Transform *= Matrix3x2.CreateTranslation(0, (float)yOffset);

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
                        line.TopLeftPosition.X,
                        yOffset + line.TopLeftPosition.Y,
                        line.BottomRightPosition.X - line.TopLeftPosition.X,
                        line.BottomRightPosition.Y - line.TopLeftPosition.Y)
                        .Scale(1.05);

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

        #region Input Handling

        private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            _layoutDirty = true;
            _canvas?.Invalidate();
        }

        private void OnCanvasPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;
            var props = e.GetCurrentPoint(_canvas).Properties;
            double delta = props.MouseWheelDelta * WheelScrollPixels * _cachedScrollSensitivity / 120.0;
            _targetScrollY += delta;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            _flingY = 0;
        }

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas == null) return;
            _canvas.CapturePointer(e.Pointer);
            _pointerCaptured = true;
            _pointerLastY = e.GetCurrentPoint(_canvas).Position.Y;
            _flingY = 0;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas == null) return;
            var pos = e.GetCurrentPoint(_canvas).Position;
            _lastMousePos = pos;

            if (_pointerCaptured)
            {
                double dy = pos.Y - _pointerLastY;
                _targetScrollY += dy;
                _pointerLastY = pos.Y;
                _flingY = dy;
            }
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas == null) return;
            _canvas.ReleasePointerCapture(e.Pointer);
            _pointerCaptured = false;
        }

        private void OnCanvasPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _pointerCaptured = false;
        }

        private void OnCanvasPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isMouseInLyricsArea = false;
        }

        private void OnCanvasPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isMouseInLyricsArea = true;
        }

        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;

            double playingLineTopOffsetFactor = 0.35;
            int hovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                _renderLines, true, _lastMousePos, _smoothedScrollY,
                _canvas?.Size.Height ?? 400, playingLineTopOffsetFactor);

            if (hovered >= 0 && hovered < _renderLines.Count)
            {
                var line = _renderLines[hovered];

                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLines, hovered);
                if (targetScroll.HasValue)
                {
                    _targetScrollY = targetScroll.Value;
                    _smoothedScrollY = targetScroll.Value;
                    _userScrolling = false;
                    _userScrollCooldownSec = 0;
                    _flingY = 0;
                }

                LyricLineClicked?.Invoke(this, TimeSpan.FromMilliseconds(line.StartMs));
            }
        }

        #endregion
    }
}
