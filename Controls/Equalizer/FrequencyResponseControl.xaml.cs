using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Controls.Equalizer;

/// <summary>Shared EQ + FIR preview. All DSP work is off the UI thread; stale work cannot redraw a newer edit.</summary>
public sealed partial class FrequencyResponseControl : UserControl
{
    public WinUIMusicPlayer.ViewModel.FrequencyResponseViewModel ViewModel { get; } = new();
    private WinUIMusicPlayer.ViewModel.FrequencyResponseViewModel.Preview? _preview => ViewModel.Current;
    private CurvePoint[] _points = [];
    private int _selected = -1;
    private bool _dragging;
    private int _rate = 48000;
    private double _axisLimit = 24;
    public string PlotName => ToolUtils.GetString("ResponsePlotName");
    public event Action<int>? PointSelected;
    public event Action<int, double, double>? PointMoved;
    public FrequencyResponseControl()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Load();
        Unloaded += (_, _) => ViewModel.Unload();
        ViewModel.Updated += () =>
        {
            if (_preview != null)
            {
                _rate = _preview.Rate;
                _axisLimit = Math.Max(24, Math.Ceiling(_preview.Combined.Concat(_preview.Fir).Append(_preview.Eq).SelectMany(x => x).Max(Math.Abs) / 12) * 12);
            }
            Draw();
        };
        ActualThemeChanged += (_, _) => Draw();
    }
    public void SetPoints(CurvePoint[] points, int selected) { _points = points; _selected = selected; Draw(); }
    public void Refresh() => ViewModel.Refresh();

    private Brush Brush(string key) => key switch
    {
        "TextFillColorPrimaryBrush" => (Brush)Resources["ResponseInk"],
        "TextFillColorSecondaryBrush" => (Brush)Resources["ResponseSecondary"],
        "ControlStrongStrokeColorDefaultBrush" => (Brush)Resources["ResponseGrid"],
        "ControlFillColorDefaultBrush" => (Brush)Resources["ResponseNode"],
        // Reuse the theme-bound control brush; AccentTextFillColorPrimary is not a Color resource.
        _ => ShowCombined.Foreground
    };
    private double MaxFrequency => Math.Min(20000, _rate * 0.499);
    private double X(double hz) => 42 + Math.Log(hz / 20) / Math.Log(MaxFrequency / 20) * Math.Max(1, Plot.ActualWidth - 56);
    private double AxisLimit => _axisLimit;
    private double Y(double db) => 18 + (AxisLimit - Math.Clamp(db, -AxisLimit, AxisLimit)) / (AxisLimit * 2) * 180;
    private void Draw()
    {
        if (Plot == null || Plot.ActualWidth < 100) return;
        Plot.Children.Clear();
        foreach (double db in new[] { -AxisLimit, -AxisLimit / 2, 0, AxisLimit / 2, AxisLimit })
        {
            Plot.Children.Add(new Line { X1 = 42, X2 = Plot.ActualWidth - 14, Y1 = Y(db), Y2 = Y(db),
                Stroke = Brush("ControlStrongStrokeColorDefaultBrush"), StrokeThickness = db == 0 ? 1 : 0.5 });
            Label(db.ToString("+0;-0;0"), 3, Y(db) - 8);
        }
        foreach (double hz in new[] { 20.0, 100, 1000, 10000, 20000 })
        {
            if (hz > MaxFrequency) continue;
            Plot.Children.Add(new Line { X1 = X(hz), X2 = X(hz), Y1 = 18, Y2 = 198,
                Stroke = Brush("ControlStrongStrokeColorDefaultBrush"), StrokeThickness = 0.5 });
            Label(hz >= 1000 ? hz / 1000 + "k" : hz.ToString(), X(hz) - 10, 205);
        }
        if (_preview != null)
        {
            int ch = Math.Max(0, ChannelPicker.SelectedIndex);
            if (ShowEq.IsChecked == true) Curve(_preview.Eq, "TextFillColorPrimaryBrush", 1.3, [5, 3]);
            if (ShowFir.IsChecked == true) Curve(_preview.Fir[ch], "TextFillColorSecondaryBrush", 1.5, [1, 3]);
            if (ShowCombined.IsChecked == true) Curve(_preview.Combined[ch], "SystemControlForegroundAccentBrush", 2.5, null);
        }
        if (_points.Length > 0)
        {
            var line = new Polyline { Stroke = Brush("TextFillColorSecondaryBrush"), StrokeThickness = 1, StrokeDashArray = [2, 3] };
            for (int i = 0; i < 256; i++)
            {
                double hz = 20 * Math.Pow(MaxFrequency / 20, i / 255.0);
                line.Points.Add(new(X(hz), Y(CorrectionCurve.Evaluate(_points, hz))));
            }
            Plot.Children.Add(line);
            for (int i = 0; i < _points.Length; i++)
            {
                if (_points[i].Frequency > MaxFrequency) continue;
                var node = new Ellipse { Width = 12, Height = 12, Fill = Brush(i == _selected ? "SystemControlForegroundAccentBrush" : "ControlFillColorDefaultBrush"),
                    Stroke = Brush("TextFillColorPrimaryBrush"), StrokeThickness = 1.5 };
                Canvas.SetLeft(node, X(_points[i].Frequency) - 6); Canvas.SetTop(node, Y(_points[i].GainDb) - 6); Plot.Children.Add(node);
            }
        }
    }
    private void Curve(double[] values, string brush, double width, DoubleCollection? dash)
    {
        var line = new Polyline { Stroke = Brush(brush), StrokeThickness = width };
        if (dash != null) line.StrokeDashArray = dash;
        for (int i = 0; i < values.Length; i++) line.Points.Add(new(X(_preview!.Frequencies[i]), Y(values[i])));
        Plot.Children.Add(line);
    }
    private void Label(string text, double x, double y)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Foreground = Brush("TextFillColorSecondaryBrush") };
        Canvas.SetLeft(label, x); Canvas.SetTop(label, y); Plot.Children.Add(label);
    }
    private void PlotPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Plot);
        if (!p.Properties.IsLeftButtonPressed) return;
        for (int i = 0; i < _points.Length; i++)
            if (Math.Abs(p.Position.X - X(_points[i].Frequency)) < 15 && Math.Abs(p.Position.Y - Y(_points[i].GainDb)) < 15)
            {
                _selected = i; _dragging = true; Plot.CapturePointer(e.Pointer); PointSelected?.Invoke(i); Draw(); e.Handled = true; break;
            }
    }
    private void PlotMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _selected < 0) return;
        var p = e.GetCurrentPoint(Plot).Position;
        double hz = 20 * Math.Pow(MaxFrequency / 20, Math.Clamp((p.X - 42) / Math.Max(1, Plot.ActualWidth - 56), 0, 1));
        double db = Math.Clamp(AxisLimit - (p.Y - 18) / 180 * (AxisLimit * 2), -12, 12);
        PointMoved?.Invoke(_selected, hz, db); e.Handled = true;
    }
    private void PlotReleased(object sender, PointerRoutedEventArgs e) { _dragging = false; Plot.ReleasePointerCaptures(); }
    private void PlotSizeChanged(object sender, SizeChangedEventArgs e) => Draw();
    private void ChannelChanged(object sender, SelectionChangedEventArgs e) => Draw();
    private void LayerChanged(object sender, RoutedEventArgs e) => Draw();
}
