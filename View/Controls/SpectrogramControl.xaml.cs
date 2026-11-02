using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.Controls;

/// <summary>Win2D 按需绘制声谱与刻度，Composition 只移动播放线；设置、输入和任务均由 ViewModel 拥有。</summary>
public sealed partial class SpectrogramControl : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel),
        typeof(SpectrogramViewModel), typeof(SpectrogramControl), new PropertyMetadata(null, ModelChanged));
    public SpectrogramViewModel? ViewModel { get => (SpectrogramViewModel?)GetValue(ViewModelProperty); set => SetValue(ViewModelProperty, value); }
    private CanvasControl? _canvas;
    private CanvasBitmap? _bitmap;
    private CanvasTextFormat? _text;
    private CancellationTokenSource? _pixels;
    private Visual? _cursor;
    private ScalarKeyFrameAnimation? _cursorAnimation;
    private LinearEasingFunction? _cursorEasing;
    private readonly AccessibilitySettings _accessibility = new();
    private int _version;
    private double _lastPosition;
    private bool _wasPlaying;
    private bool _panning;
    private double _dragX;
    private Rect Plot => new(48, 28, Math.Max(1, ChartHost.ActualWidth - 64), Math.Max(1, ChartHost.ActualHeight - 60));

    public SpectrogramControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => { RefreshPixels(); UpdateCursor(); };
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => { RefreshPixels(); UpdateCursor(); });
        ChartHost.SizeChanged += (_, _) => { _canvas?.Invalidate(); UpdateCursor(); };
    }
    private static void ModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (SpectrogramControl)sender;
        if (args.OldValue is SpectrogramViewModel old) old.PropertyChanged -= control.ModelPropertyChanged;
        if (control.IsLoaded && args.NewValue is SpectrogramViewModel current) current.PropertyChanged += control.ModelPropertyChanged;
        if (args.NewValue is null)
        {
            control.Bindings.StopTracking();
            control.DetachCommands();
        }
        else control.Bindings.Update();
        control.RefreshPixels();
        control.UpdateCursor();
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not null) { ViewModel.PropertyChanged -= ModelPropertyChanged; ViewModel.PropertyChanged += ModelPropertyChanged; }
        _canvas = new CanvasControl();
        _canvas.CreateResources += Canvas_CreateResources;
        _canvas.Draw += Canvas_Draw;
        ChartHost.Children.Insert(0, _canvas);
        _cursor = ElementCompositionPreview.GetElementVisual(PlaybackCursor);
        ElementCompositionPreview.SetIsTranslationEnabled(PlaybackCursor, true);
        _cursorAnimation = _cursor.Compositor.CreateScalarKeyFrameAnimation();
        _cursorAnimation.Duration = TimeSpan.FromMilliseconds(120);
        _cursorEasing = _cursor.Compositor.CreateLinearEasingFunction();
        Bindings.Update();
        UpdateCursor();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (ViewModel is not null) ViewModel.PropertyChanged -= ModelPropertyChanged;
        Bindings.StopTracking();
        DetachCommands();
        _version++;
        _pixels?.Cancel();
        _cursor?.StopAnimation("Translation.X");
        _cursorAnimation?.Dispose();
        _cursorAnimation = null;
        _cursorEasing?.Dispose();
        _cursorEasing = null;
        _cursor = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _text?.Dispose();
        _text = null;
        if (_canvas is { } canvas)
        {
            canvas.CreateResources -= Canvas_CreateResources;
            canvas.Draw -= Canvas_Draw;
            canvas.RemoveFromVisualTree();
            ChartHost.Children.Remove(canvas);
            _canvas = null;
        }
    }
    private void DetachCommands()
    {
        // 停止跟踪不会清空原生 Button.Command。释放命令桥对旧 VM 的引用，Loaded 时由 x:Bind 恢复。
        ResetButton.Command = null;
        RetryButton.Command = null;
    }
    private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        _text?.Dispose();
        _text = new CanvasTextFormat { FontSize = 11, FontFamily = FontFamily.Source };
        RefreshPixels();
    }
    private void ModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SpectrogramViewModel.Data) or nameof(SpectrogramViewModel.Channel)
            or nameof(SpectrogramViewModel.MinimumDb) or nameof(SpectrogramViewModel.MaximumDb)) RefreshPixels();
        if (args.PropertyName is nameof(SpectrogramViewModel.Zoom) or nameof(SpectrogramViewModel.ViewStart)) { _canvas?.Invalidate(); UpdateCursor(); }
        if (args.PropertyName is nameof(SpectrogramViewModel.PositionSeconds) or nameof(SpectrogramViewModel.IsPlaying)
            or nameof(SpectrogramViewModel.HasPlaybackCursor) or nameof(SpectrogramViewModel.Data)) UpdateCursor();
    }
    private async void RefreshPixels()
    {
        int version = ++_version;
        _pixels?.Cancel();
        if (_canvas is not { ReadyToDraw: true } canvas || !IsLoaded) return;
        if (ViewModel?.Data is not { } data)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            canvas.Invalidate();
            return;
        }
        var request = new CancellationTokenSource();
        _pixels = request;
        byte[] values = data.Values(ViewModel.Channel);
        double minimum = ViewModel.MinimumDb, maximum = ViewModel.MaximumDb;
        bool contrast = _accessibility.HighContrast;
        int height = Math.Min(1024, data.Height);
        try
        {
            var pixels = await Task.Run(() => MakePixels(data, values, height, minimum, maximum, contrast, request.Token), request.Token);
            if (version != _version || !ReferenceEquals(canvas, _canvas) || !IsLoaded) return;
            var bitmap = CanvasBitmap.CreateFromBytes(canvas, pixels, data.Width, height, Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _bitmap?.Dispose();
            _bitmap = bitmap;
            canvas.Invalidate();
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (version == _version && ReferenceEquals(canvas, _canvas) && IsLoaded)
            {
                if (canvas.Device.IsDeviceLost(exception.HResult)) canvas.Device.RaiseDeviceLost();
                else App.GetLogger<SpectrogramControl>().LogWarning(exception, "绘制声谱图失败");
            }
        }
        finally { if (ReferenceEquals(_pixels, request)) _pixels = null; request.Dispose(); }
    }
    private static byte[] MakePixels(SpectrogramData data, byte[] values, int height, double minimum, double maximum, bool contrast, CancellationToken token)
    {
        var colors = new Color[161];
        for (int i = 0; i < colors.Length; i++) colors[i] = Palette(Math.Clamp((i - 160 - minimum) / (maximum - minimum), 0, 1), contrast);
        var pixels = new byte[data.Width * height * 4];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            int first = (height - 1 - y) * data.Height / height;
            int last = Math.Max(first + 1, (height - y) * data.Height / height);
            for (int x = 0; x < data.Width; x++)
            {
                byte peak = 0;
                for (int bin = first; bin < last; bin++) peak = Math.Max(peak, values[x * data.Height + bin]);
                var color = colors[Math.Min(160, (int)peak)];
                int offset = (y * data.Width + x) * 4;
                pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = 255;
            }
        }
        return pixels;
    }
    // 科学色阶与主题强调色分开：相同强度在两种主题中保持相同颜色，HC 使用单调灰阶。
    private static Color Palette(double value, bool contrast)
    {
        if (contrast) { byte gray = (byte)(value * 255); return Color.FromArgb(255, gray, gray, gray); }
        ReadOnlySpan<uint> stops = [0x071B30, 0x145B91, 0x08A0B5, 0x66BA73, 0xF2D34F, 0xF04A34, 0xEF2929];
        double scaled = value * (stops.Length - 1);
        int index = Math.Min(stops.Length - 2, (int)scaled);
        double t = scaled - index;
        uint a = stops[index], b = stops[index + 1];
        return Color.FromArgb(255, (byte)(((a >> 16) & 255) * (1 - t) + ((b >> 16) & 255) * t),
            (byte)(((a >> 8) & 255) * (1 - t) + ((b >> 8) & 255) * t), (byte)((a & 255) * (1 - t) + (b & 255) * t));
    }
    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap is null || ViewModel?.Data is not { } data || _text is null) return;
        var drawing = args.DrawingSession;
        var plot = Plot;
        drawing.DrawImage(_bitmap, plot, new Rect(ViewModel.ViewStart * data.Width, 0, data.Width / ViewModel.Zoom, _bitmap.Size.Height),
            1, CanvasImageInterpolation.NearestNeighbor);
        var foreground = (Foreground as SolidColorBrush)?.Color ?? Microsoft.UI.Colors.Gray;
        var grid = foreground;
        grid.A = 45;
        for (int i = 0; i <= 4; i++)
        {
            float y = (float)(plot.Bottom - plot.Height * i / 4);
            drawing.DrawLine((float)plot.Left, y, (float)plot.Right, y, grid);
            drawing.DrawText($"{data.SampleRate / 2000d * i / 4:0.#}k", 0, y - 7, foreground, _text);
            float x = (float)(plot.Left + plot.Width * i / 4);
            double seconds = (ViewModel.ViewStart + i / 4d / ViewModel.Zoom) * data.Duration;
            drawing.DrawText($"{(int)seconds / 60}:{(int)seconds % 60:00}", Math.Clamp(x - 14, 0, (float)ChartHost.ActualWidth - 34), (float)plot.Bottom + 6, foreground, _text);
        }
        drawing.DrawText($"{ViewModel.MinimumDb:0} … {ViewModel.MaximumDb:0} dBFS", (float)plot.Left, 0, foreground, _text);
        for (int i = 0; i < 40; i++)
            drawing.FillRectangle((float)plot.Right - 80 + i * 2, 4, 2, 6, Palette(i / 39d, _accessibility.HighContrast));
    }
    private void UpdateCursor()
    {
        if (_cursor is null) return;
        var vm = ViewModel;
        var plot = Plot;
        double fraction = vm?.Data is { } data ? (vm.PositionSeconds / data.Duration - vm.ViewStart) * vm.Zoom : -1;
        bool visible = vm is { HasPlaybackCursor: true, Data: not null } && fraction is >= 0 and <= 1;
        PlaybackCursor.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _cursor.StopAnimation("Translation.X");
        if (!visible) { _wasPlaying = false; return; }
        PlaybackCursor.Height = plot.Height;
        PlaybackCursor.Margin = new Thickness(plot.Left, plot.Top, 0, 0);
        float x = (float)(fraction * plot.Width);
        // 连续进度通知间插值；暂停、Seek、切歌直接定位，不用定时器重画声谱。
        bool smooth = vm!.IsPlaying && _wasPlaying && vm.PositionSeconds >= _lastPosition && vm.PositionSeconds - _lastPosition < .75;
        if (smooth && _cursorAnimation is not null)
        {
            _cursorAnimation.InsertKeyFrame(1, x, _cursorEasing);
            _cursor.StartAnimation("Translation.X", _cursorAnimation);
        }
        else _cursor.Properties.InsertVector3("Translation", new Vector3(x, 0, 0));
        _lastPosition = vm.PositionSeconds;
        _wasPlaying = vm.IsPlaying;
    }
    private bool Relative(Point point, out double x, out double y)
    {
        var plot = Plot;
        x = (point.X - plot.Left) / plot.Width;
        y = (point.Y - plot.Top) / plot.Height;
        return x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }
    private void Chart_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_panning)
        {
            double current = args.GetCurrentPoint(ChartHost).Position.X;
            ViewModel?.Pan((_dragX - current) / Plot.Width);
            _dragX = current;
            return;
        }
        if (Relative(args.GetCurrentPoint(ChartHost).Position, out double x, out double y)) ViewModel?.Inspect(x, y);
        else ViewModel?.ClearInspection();
    }
    private void Chart_PointerExited(object sender, PointerRoutedEventArgs args) => ViewModel?.ClearInspection();
    private void Chart_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(ChartHost);
        ChartHost.Focus(FocusState.Pointer);
        if (ViewModel is not { Zoom: > 1 } || !point.Properties.IsLeftButtonPressed || !Relative(point.Position, out _, out _)) return;
        _panning = ChartHost.CapturePointer(args.Pointer);
        _dragX = point.Position.X;
    }
    private void Chart_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _panning = false;
        ChartHost.ReleasePointerCapture(args.Pointer);
    }
    private void Chart_PointerCaptureLost(object sender, PointerRoutedEventArgs args) => _panning = false;
    private void Chart_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(ChartHost);
        if (!Relative(point.Position, out double x, out _)) return;
        ViewModel?.ZoomAt(x, point.Properties.MouseWheelDelta > 0 ? 1.25 : .8);
        args.Handled = true;
    }
    private void Chart_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (Relative(args.GetPosition(ChartHost), out double x, out _)) ViewModel?.Seek(x);
    }
    private void Chart_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        switch (args.Key)
        {
            case VirtualKey.Add: case VirtualKey.Up: ViewModel?.ZoomAt(.5, 1.25); break;
            case VirtualKey.Subtract: case VirtualKey.Down: ViewModel?.ZoomAt(.5, .8); break;
            case VirtualKey.Left: ViewModel?.Pan(-.1); break;
            case VirtualKey.Right: ViewModel?.Pan(.1); break;
            case VirtualKey.Home: ViewModel?.ResetViewCommand.Execute(null); break;
            default: return;
        }
        args.Handled = true;
    }
    public Visibility BusyVisibility(bool busy) => busy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility(bool busy, string? status) => !busy && !string.IsNullOrEmpty(status) ? Visibility.Visible : Visibility.Collapsed;
}
