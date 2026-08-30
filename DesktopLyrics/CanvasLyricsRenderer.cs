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
    /// Win2D 逐字歌词渲染器（薄宿主）：只渲染当前一行 + 翻译，单行块垂直居中，逐字扫光。
    /// 直接组装 External 库内部件（RenderLyricsLine / LyricsLineRenderer / LyricsLayoutManager，
    /// 三者均零静态总线依赖、全参数注入），绕开 LyricsRenderCoordinator 的整页滚动逻辑；
    /// 样式完全来自 <see cref="DesktopLyricsStyle"/>（不消费主界面 LyricsSettingsBus），
    /// 数据经 <see cref="IDesktopLyricsRenderer"/> 的 Set* 由宿主窗口推送（总线转发零改动）。
    /// 描边实现：EnsureCaches 的 CachedStroke 是白描边命令列表，宿主先以黑色
    /// <see cref="RenderLyricsLine.UnplayedStrokeTint"/> 垫底再画逐字填充 region——
    /// 播放中的填充路径（DrawSubLineRegions/DrawFullLineRegion）不含描边层，
    /// 垫底后扫光在黑描边之上进行，获得与文本渲染器一致的黑边可读性。
    /// 线程模型：CanvasAnimatedControl 的 Update/Draw 在渲染线程回调，Set* 在 UI 线程调用；
    /// 与 LyricsRenderCoordinator 相同，仅依赖字段原子赋值传递状态，不持锁
    /// （Set* 只写字段/标志，Win2D 资源的全部创建与释放都在 Update/Draw 回调内）。
    /// </summary>
    public sealed class CanvasLyricsRenderer : IDesktopLyricsRenderer
    {
        private const double SyncThresholdMs = 250;   // 内部时钟与外部时间的硬同步阈值（coordinator 同款）
        private const double UnplayedOpacity = 0.5;   // 未播放部分透明度（主界面默认行为）
        private const double SecondaryOpacity = 0.6;  // 翻译行透明度（与文本渲染器 _transOpacity 一致）
        private const int TargetFrameRate = 60;
        private const string DefaultFontFamily = "Segoe UI";

        private readonly CanvasAnimatedControl _canvas = new() { ClearColor = Colors.Transparent };
        private readonly LyricsLineRenderer _lineRenderer = new();

        private List<LyricLine>? _lyrics;
        private RenderLyricsLine? _currentLine;   // 仅当前行的 Win2D 资源；切行/样式/尺寸变化时整体重建
        private int _currentIndex = -1;
        private bool _lyricsChanged;
        private bool _layoutDirty = true;
        private double _lastLayoutWidth;
        private double _lastLayoutHeight;

        private double _internalTimeMs;
        private double _lastExternalTimeMs;
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
            // 字号/字体/字重/描边/翻译都影响布局，统一标记下帧重建（颜色不触发，仅每帧赋值）
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
            _canvas.Paused = true;
            _canvas.CreateResources -= OnCanvasCreateResources;
            _canvas.Update -= OnCanvasUpdate;
            _canvas.Draw -= OnCanvasDraw;
            DisposeCurrentLine();
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
            if (_lyricsChanged)
            {
                DisposeCurrentLine();
                _currentIndex = -1;
                _lyricsChanged = false;
                _layoutDirty = true;
            }

            // 时间平滑（coordinator OnUpdate 同款）：播放中内部时钟按帧自增，
            // 与外部时间偏差超阈值才硬同步；暂停时直接跟随外部时间
            double externalTimeMs = _lastTotalMs;
            if (_isPlaying)
            {
                if (!_timeSyncValid || Math.Abs(externalTimeMs - _lastExternalTimeMs) > SyncThresholdMs)
                    _internalTimeMs = externalTimeMs;
                else
                    _internalTimeMs += args.Timing.ElapsedTime.TotalMilliseconds;
                _lastExternalTimeMs = externalTimeMs;
                _timeSyncValid = true;
            }
            else
            {
                _internalTimeMs = externalTimeMs;
                _lastExternalTimeMs = externalTimeMs;
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

            _currentLine?.Update(args.Timing.ElapsedTime);   // 推进透明度等 ValueTransition
        }

        private void OnCanvasDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
        {
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

                // 单行块垂直居中：MeasureAndArrange 自上而下定位，BottomRightPosition.Y 即块高
                float offsetY = (float)Math.Max(0, (_canvas.Size.Height - line.BottomRightPosition.Y) / 2);
                var prevTransform = ds.Transform;
                ds.Transform *= Matrix3x2.CreateTranslation(0, offsetY);

                // 黑描边垫底：播放中的填充 region 不含描边，垫底后扫光覆盖在描边之上；
                // 暂停/翻译路径的 UnplayedComposite 自带描边，同色重复绘制无视觉差异
                if (strokeWidth > 0 && line.UnplayedStrokeTint != null)
                    ds.DrawImage(line.UnplayedStrokeTint);

                _lineRenderer.IsPlaying = line.GetIsPlaying(currentTimeMs);
                _lineRenderer.CurrentProgressMs = currentTimeMs;
                _lineRenderer.Line = line;
                _lineRenderer.PlayedFillColor = _color;
                _lineRenderer.UnplayedFillColor = _color;
                _lineRenderer.IsGlowEnabled = false;
                _lineRenderer.IsScaleEnabled = false;
                _lineRenderer.IsFloatEnabled = false;
                _lineRenderer.Draw(sender, ds);
                ds.Transform = prevTransform;
            }
            catch (ObjectDisposedException)
            {
                // 设备丢失（coordinator 同款兜底）：弃缓存，下帧经 _layoutDirty 重建
                line.DisposeCaches();
                _layoutDirty = true;
            }
        }

        // ── 行构建与释放 ─────────────────────────────────────────────────────

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
                line.UnplayedPrimaryOpacityTransition.Start(UnplayedOpacity);
                line.SecondaryOpacityTransition.Start(SecondaryOpacity);
                _currentLine = line;
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
