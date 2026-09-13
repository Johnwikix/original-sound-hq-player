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
    private sealed record Preview(double[] Frequencies, double[] Eq, double[][] Fir, double[][] Combined, int Rate, double AutoGain);
    private Preview? _preview;
    private CancellationTokenSource? _cancellation;
    private DspSettings? _settingsOverride;
    private CurvePoint[] _points = [];
    private int _selected = -1;
    private bool _dragging, _loaded;
    private int _rate = 48000;
    private double _axisLimit = 24;
    private string? _cacheKey;
    private ImpulseResponse? _cacheImpulse;
    private Complex[][]? _cacheSpectra;
    public string PlotName => ToolUtils.GetString("ResponsePlotName");
    public event Action<int>? PointSelected;
    public event Action<int, double, double>? PointMoved;

    public FrequencyResponseControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            App.Services.GetRequiredService<IpcService>().DspStateChanged += StateChanged;
            AppSettings.EqUpdated += EqChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            App.Services.GetRequiredService<IpcService>().DspStateChanged -= StateChanged;
            AppSettings.EqUpdated -= EqChanged;
            _cancellation?.Cancel(); _cancellation?.Dispose(); _cancellation = null;
        };
        ActualThemeChanged += (_, _) => Draw();
    }
    private void EqChanged(object? sender, EventArgs e) => Refresh(_settingsOverride);
    private void StateChanged() => DispatcherQueue.TryEnqueue(() => { if (_loaded) Refresh(_settingsOverride); });

    public void SetPoints(CurvePoint[] points, int selected)
    {
        _points = points; _selected = selected; Draw();
    }
    public async void Refresh(DspSettings? settings = null)
    {
        _settingsOverride = settings;
        if (!_loaded) return;
        _cancellation?.Cancel(); _cancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        var state = App.Services.GetRequiredService<IpcService>().CurrentDspState?.State;
        int rate = state is { SampleRate: >= 8000 } value ? value.SampleRate : 48000;
        _rate = rate;
        var dsp = (settings ?? AppSettings.Dsp).Sanitize();
        bool eqEnabled = AppSettings.IsEqualizerEnabled;
        var eq = AppSettings.EqualizerBands.Select(b => PeakCoefficients.Create(b.FrequencyHz, (float)b.GainDb, (float)b.Q, rate)).ToArray();
        string key = rate + ":" + (CorrectionCurve.UsesCurve(dsp) ? "curve:" + dsp.CurvePoints : "wave:" + dsp.ImpulsePath);
        var cachedImpulse = key == _cacheKey ? _cacheImpulse : null;
        var cachedSpectra = key == _cacheKey ? _cacheSpectra : null;
        StatusText.Text = ToolUtils.GetString("ResponsePreparing");
        try
        {
            await Task.Delay(150, token);
            var result = await Task.Run(() =>
            {
                ImpulseResponse? impulse = cachedImpulse;
                Complex[][]? spectra = cachedSpectra;
                bool hasIr = CorrectionCurve.UsesCurve(dsp) || !string.IsNullOrEmpty(dsp.ImpulsePath);
                if (dsp.ConvolutionEnabled && hasIr && impulse == null)
                {
                    impulse = CorrectionCurve.Prepare(dsp, rate, token);
                    spectra = impulse.Channels.Select(ResponseMath.Spectrum).ToArray();
                }
                token.ThrowIfCancellationRequested();
                double automatic = dsp.AutoConvolutionHeadroom && CorrectionCurve.UsesCurve(dsp) && spectra != null
                    ? -20 * Math.Log10(Math.Max(1, spectra.SelectMany(x => x.Take(x.Length / 2 + 1)).Max(x => x.Magnitude))) : 0;
                double[] frequencies = new double[384], equalizer = new double[384];
                double[][] fir = [new double[384], new double[384]], combined = [new double[384], new double[384]];
                double maximum = Math.Min(20000, rate * 0.499);
                for (int i = 0; i < frequencies.Length; i++)
                {
                    double hz = 20 * Math.Pow(maximum / 20, i / (double)(frequencies.Length - 1));
                    frequencies[i] = hz;
                    equalizer[i] = eqEnabled ? eq.Sum(b => b.ResponseDb(hz, rate)) : 0;
                    for (int ch = 0; ch < 2; ch++)
                    {
                        fir[ch][i] = dsp.ConvolutionEnabled && spectra != null
                            ? ResponseMath.MagnitudeDb(spectra[Math.Min(ch, spectra.Length - 1)], rate, hz) + dsp.ConvolutionTrimDb + automatic : 0;
                        combined[ch][i] = equalizer[i] + fir[ch][i] + dsp.HeadroomDb;
                    }
                }
                return (Preview: new Preview(frequencies, equalizer, fir, combined, rate, automatic), Impulse: impulse, Spectra: spectra);
            }, token);
            if (token.IsCancellationRequested || !_loaded) return;
            _preview = result.Preview;
            _axisLimit = Math.Max(24, Math.Ceiling(_preview.Combined.Concat(_preview.Fir).Append(_preview.Eq).SelectMany(x => x).Max(Math.Abs) / 12) * 12);
            _cacheKey = key; _cacheImpulse = result.Impulse; _cacheSpectra = result.Spectra;
            bool bypass = state == null || state.Value.SampleRate <= 0 || state.Value.RenderKind != 0 || !state.Value.IsEnabled || state.Value.Channels > 2;
            StatusText.Text = string.Format(ToolUtils.GetString(bypass ? "ResponseBypass" : "ResponseReady"), rate / 1000.0, result.Preview.AutoGain);
            Draw();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (token.IsCancellationRequested || !_loaded) return;
            _preview = null; Draw(); StatusText.Text = ToolUtils.GetString("ResponseFailed");
        }
    }

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
