using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;
using AnimatedWin2dControls.Messages;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public sealed class UnifiedLyricsCanvasControlV2 : Control,
        IRecipient<CurrentPlayingTimeMessage>,
        IRecipient<IsPlayingMessage>,
        IRecipient<OffsetMsMessage>,
        IRecipient<LyricsFontSizeMessage>,
        IRecipient<UILyricsMessage>,
        IRecipient<LyricsSettingsSyncMessage>
    {
        public event EventHandler<TimeSpan>? LyricLineClicked;
        public event EventHandler<Exception>? RenderError;

        private CanvasAnimatedControl? _canvas;
        private List<RenderLyricsLine> _renderLines = [];
        private List<RenderLyricsLine>? _pendingDisposeLines;
        private bool _layoutDirty = true;
        private bool _shutdown;
        private int _currentLineIndex = -1;
        private int _lastCurrentLineIndex = -1;

        private LyricsSynchronizer _synchronizer = new();
        private LyricsAnimator _animator = new();
        private LyricsLineRendererV2 _lineRenderer = new();
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
        private const double SyncThresholdMs = 200.0;

        public double AutoScrollSpeed = 4.0;
        public double UserScrollReturnSpeed = 12.0;
        public double WheelScrollPixels = 80.0;

        private bool _pointerCaptured;
        private double _pointerLastY;
        private double _flingY;
        private const double FlingDecay = 0.92;
        private const double FlingStopThreshold = 5.0;

        private int _hoveredLineIndex = -1;
        private bool _isMouseInLyricsArea;
        private Point _lastMousePos;
        private bool _hoverDirty;

        private int _cachedVisibleStart;
        private int _cachedVisibleEnd;
        private double _cachedEdgeFadeWidth;
        private double _cachedEdgeFadeHeight;

        private Color _playedColor = Colors.White;
        private Color _unplayedColor = Color.FromArgb(80, 255, 255, 255);

        // ── 渲染线程安全缓存 ────────────────────────────────────────────────
        private double _cachedLyricsFontSize = 36.0;
        private string _cachedFontFamilyName = "Segoe UI";
        private CanvasHorizontalAlignment _cachedLyricsTextAlignment = CanvasHorizontalAlignment.Left;
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

        public UnifiedLyricsCanvasControlV2()
        {
            DefaultStyleKey = typeof(UnifiedLyricsCanvasControlV2);
            SizeChanged += OnControlSizeChanged;
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;

            _canvasYScrollTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
            _mouseYScrollTransition = new(0, EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.3);
        }

        public void PrepareForShutdown()
        {
            if (_shutdown) return;
            _shutdown = true;

            if (_canvas != null)
            {
                _canvas.Paused = true;
                _canvas.Update -= OnCanvasUpdate;
                _canvas.Draw -= OnCanvasDraw;
                _canvas.CreateResources -= OnCanvasCreateResources;
                _canvas.PointerWheelChanged -= OnCanvasPointerWheelChanged;
                _canvas.PointerPressed -= OnCanvasPointerPressed;
                _canvas.PointerMoved -= OnCanvasPointerMoved;
                _canvas.PointerReleased -= OnCanvasPointerReleased;
                _canvas.PointerCanceled -= OnCanvasPointerCanceled;
                _canvas.PointerExited -= OnCanvasPointerExited;
                _canvas.PointerEntered -= OnCanvasPointerEntered;
                _canvas.Tapped -= OnCanvasTapped;
                _canvas = null;
            }

            DisposeRenderLines();
            if (_pendingDisposeLines != null)
            {
                foreach (var line in _pendingDisposeLines)
                    line?.DisposeTextLayout();
                _pendingDisposeLines = null;
            }
            _edgeFadeMask.Dispose();
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            WeakReferenceMessenger.Default.RegisterAll(this);
            WeakReferenceMessenger.Default.Send(new RequestLyricsSettingsMessage());
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
            PrepareForShutdown();
        }

        protected override void OnApplyTemplate()
        {
            if (_canvas != null)
            {
                _canvas.Update -= OnCanvasUpdate;
                _canvas.Draw -= OnCanvasDraw;
                _canvas.CreateResources -= OnCanvasCreateResources;
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
                _canvas.CreateResources += OnCanvasCreateResources;
                _canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
                _canvas.PointerPressed += OnCanvasPointerPressed;
                _canvas.PointerMoved += OnCanvasPointerMoved;
                _canvas.PointerReleased += OnCanvasPointerReleased;
                _canvas.PointerCanceled += OnCanvasPointerCanceled;
                _canvas.PointerExited += OnCanvasPointerExited;
                _canvas.PointerEntered += OnCanvasPointerEntered;
                _canvas.Tapped += OnCanvasTapped;
                _canvas.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / _cachedTargetFrameRate);
            }

            OnIsDarkChanged(false);
        }

        private void OnCanvasCreateResources(CanvasAnimatedControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            if (_shutdown) return;
            _layoutDirty = true;
            _cachedEdgeFadeWidth = -1;
            _cachedEdgeFadeHeight = -1;
        }

        #region IRecipient Implementations

        public void Receive(CurrentPlayingTimeMessage message)
        {
            _cachedCurrentPlayingTimeMs = message.TotalMilliseconds;
        }

        public void Receive(IsPlayingMessage message)
        {
            _cachedIsPlaying = message.Value;
        }

        public void Receive(OffsetMsMessage message)
        {
            _cachedOffsetMs = message.Value;
        }

        public void Receive(LyricsFontSizeMessage message)
        {
            _cachedLyricsFontSize = message.Value;
            _layoutDirty = true;
            _canvas?.Invalidate();
        }

        public void Receive(UILyricsMessage message)
        {
            OnUILyricsChanged(message.Lines);
        }

        public void Receive(LyricsSettingsSyncMessage message)
        {
            _cachedFontFamilyName = message.FontFamilyName;
            _cachedLyricsTextAlignment = message.LyricsTextAlignment;
            _cachedScrollSensitivity = message.ScrollSensitivity;
            _cachedLyricsBlurAmount = message.LyricsBlurAmount;
            _cachedGlowAmount = message.GlowAmount;
            _cachedCharFloatAmount = message.CharFloatAmount;
            _cachedCharScaleAmount = message.CharScaleAmount;
            _cachedLongSyllableThreshold = message.LongSyllableThreshold;
            _cachedIsFadeOutEnabled = message.IsFadeOutEnabled;
            _cachedIsOutOfSightEnabled = message.IsOutOfSightEnabled;
            _cachedUnplayedOpacity = message.UnplayedOpacity;
            _cachedTranslatedOpacity = message.TranslatedOpacity;
            _cachedStrokeWidth = message.StrokeWidth;
            _cachedScrollEasingType = message.ScrollEasingType;
            _cachedScrollEasingMode = message.ScrollEasingMode;
            _cachedPlayingLineTopOffset = message.PlayingLineTopOffset;
            _cachedTargetFrameRate = message.TargetFrameRate;
            _canvas?.TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / message.TargetFrameRate);

            bool isDark = message.IsDark;
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
            _canvas?.Invalidate();
        }

        #endregion

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
            _canvas?.Invalidate();
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

            _canvas?.Invalidate();
        }

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

            if (_canvas == null || _renderLines.Count == 0) return;
            var layoutLines = _renderLines;

            if (!_layoutDirty && Math.Abs(_canvas.Size.Width - _lastLayoutWidth) < 0.5 && Math.Abs(_canvas.Size.Height - _lastLayoutHeight) < 0.5)
                return;

            _lastLayoutWidth = _canvas.Size.Width;
            _lastLayoutHeight = _canvas.Size.Height;

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
                    _canvas.Size.Width,
                    _canvas.Size.Height,
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

        private void OnCanvasUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
        {
            EnsureLayout(sender);
            var lines = _renderLines;
            if (lines.Count == 0) return;

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

            if (_userScrolling && _userScrollCooldownSec <= 0 && !_pointerCaptured)
            {
                _isUserScrollingChanged = true;
                _userScrolling = false;
                _mouseYScrollTransition.Start(0);
                _pendingMouseScrollY = 0;
            }

            if (_flingY != 0)
            {
                _mouseYScrollTransition.JumpTo(_mouseYScrollTransition.Value + _flingY * dt);
                _flingY *= 1.0 - (1.0 - FlingDecay) * dt * 60.0;
                if (Math.Abs(_flingY) < FlingStopThreshold) _flingY = 0;
            }

            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;

            double canvasHeight = _canvas?.Size.Height ?? 400;
            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;

            var visibleRange = LyricsLayoutManager.CalculateVisibleRange(
                lines, combinedScroll, 0, canvasHeight, canvasHeight, playingLineTopOffsetFactor);

            _cachedVisibleStart = visibleRange.Start;
            _cachedVisibleEnd = visibleRange.End;

            int animStart = _userScrolling ? 0 : visibleRange.Start;
            int animEnd = _userScrolling ? lines.Count - 1 : visibleRange.End;

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
                currentTimeMs);

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
                var newHovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                    lines, true, _lastMousePos, combinedScroll,
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
            new() { Position = 0.00f, Color = Microsoft.UI.Colors.Transparent },
            new() { Position = 0.05f, Color = Microsoft.UI.Colors.White },
            new() { Position = 0.35f, Color = Microsoft.UI.Colors.White },
            new() { Position = 0.65f, Color = Microsoft.UI.Colors.White },
            new() { Position = 0.95f, Color = Microsoft.UI.Colors.White },
            new() { Position = 1.00f, Color = Microsoft.UI.Colors.Transparent },
        };

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            ds.Clear(Microsoft.UI.Colors.Transparent);

            if (_renderLines.Count == 0) return;

            double canvasHeight = (float)sender.Size.Height;
            double canvasWidth = (float)sender.Size.Width;

            if (Math.Abs(_cachedEdgeFadeWidth - canvasWidth) > 0.5f || Math.Abs(_cachedEdgeFadeHeight - canvasHeight) > 0.5f)
            {
                _cachedEdgeFadeWidth = canvasWidth;
                _cachedEdgeFadeHeight = canvasHeight;
                _edgeFadeMask.Update(sender, new Rect(0, 0, canvasWidth, canvasHeight), s_edgeFadeStops, true);
            }

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
            _lineRenderer.ClearSharedResources();
            var lines = _renderLines;
            if (lines.Count == 0) return;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;

            int startIdx = Math.Max(0, _cachedVisibleStart - 3);
            int endIdx = Math.Min(lines.Count - 1, _cachedVisibleEnd + 3);

            double yOffsetBase = canvasHeight * playingLineTopOffsetFactor + combinedScroll;
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
                        line.TopLeftPosition.X - 20,
                        yOffset + line.TopLeftPosition.Y - 20,
                        line.BottomRightPosition.X - line.TopLeftPosition.X + 40,
                        line.BottomRightPosition.Y - line.TopLeftPosition.Y + 40);

                    double cw = sender.Size.Width;
                    if (hoverRect.X < 0) { hoverRect.Width += hoverRect.X; hoverRect.X = 0; }
                    if (hoverRect.X + hoverRect.Width > cw) hoverRect.Width = cw - hoverRect.X;
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
            _pendingMouseScrollY += delta;
            _mouseYScrollTransition.Start(_pendingMouseScrollY);
            _isUserScrollingChanged = !_userScrolling;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            _flingY = 0;
            _canvas?.Invalidate();
            e.Handled = true;
        }

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas == null) return;
            _canvas.CapturePointer(e.Pointer);
            _pointerCaptured = true;
            _pointerLastY = e.GetCurrentPoint(_canvas).Position.Y;
            _flingY = 0;
            _isUserScrollingChanged = !_userScrolling;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            e.Handled = true;
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas == null) return;
            var pos = e.GetCurrentPoint(_canvas).Position;
            _lastMousePos = pos;
            _hoverDirty = true;

            if (_pointerCaptured)
            {
                double dy = pos.Y - _pointerLastY;
                _pendingMouseScrollY += dy;
                _mouseYScrollTransition.Start(_pendingMouseScrollY);
                _pointerLastY = pos.Y;
                _flingY = dy;
                _canvas.Invalidate();
            }
            e.Handled = true;
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
            _hoverDirty = true;
        }

        private void OnCanvasPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isMouseInLyricsArea = true;
            _hoverDirty = true;
        }

        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_renderLines.Count == 0) return;

            double playingLineTopOffsetFactor = _cachedPlayingLineTopOffset;
            double combinedScroll = _smoothedScrollY + _mouseYScrollTransition.Value;
            int hovered = LyricsLayoutManager.FindMouseHoverLineIndex(
                _renderLines, true, _lastMousePos, combinedScroll,
                _canvas?.Size.Height ?? 400, playingLineTopOffsetFactor);

            if (hovered >= 0 && hovered < _renderLines.Count)
            {
                var line = _renderLines[hovered];

                var targetScroll = LyricsLayoutManager.CalculateTargetScrollOffset(_renderLines, hovered);
                if (targetScroll.HasValue)
                {
                    _canvasYScrollTransition.JumpTo(targetScroll.Value);
                    _smoothedScrollY = targetScroll.Value;
                    _mouseYScrollTransition.Start(0);
                    _pendingMouseScrollY = 0;
                    _userScrolling = false;
                    _userScrollCooldownSec = 0;
                    _flingY = 0;
                }
                var time = line.StartMs + _cachedOffsetMs;
                if (time < 0) time = 0;
                LyricLineClicked?.Invoke(this, TimeSpan.FromMilliseconds(time));
            }
        }

        #endregion
    }
}
