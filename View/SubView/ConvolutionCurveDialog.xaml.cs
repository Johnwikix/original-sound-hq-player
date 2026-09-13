using BassPlayerIpc.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.View.SubView;

public sealed record CurvePreset(string Name, string Points, bool AutoHeadroom, double TrimDb);
[JsonSerializable(typeof(List<CurvePreset>))]
internal partial class CurvePresetJsonContext : JsonSerializerContext { }

public sealed partial class ConvolutionCurveDialog : ContentDialog
{
    private readonly List<CurvePoint> _points;
    private readonly DispatcherQueueTimer _timer;
    private readonly IpcService _ipc;
    private List<CurvePreset> _presets = [];
    private bool _syncing = true, _open, _saving;
    private int _selected;
    private readonly DspSettings _initial;
    public string TitleText => ToolUtils.GetString("CurveEditorTitle");
    public string ApplyText => ToolUtils.GetString("CurveApply");
    public string CancelText => ToolUtils.GetString("CurveCancel");
    private static string PresetPath => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "ConvolutionCurves.json");
    public DspSettings Draft => _initial with
    {
        ConvolutionSource = ConvolutionSource.Curve, CurvePoints = CorrectionCurve.Encode(_points), ConvolutionEnabled = true,
        AutoConvolutionHeadroom = AutoHeadroom.IsChecked == true,
        ConvolutionTrimDb = double.IsFinite(TrimInput.Value) ? TrimInput.Value : _initial.ConvolutionTrimDb
    };

    public ConvolutionCurveDialog()
    {
        _initial = AppSettings.Dsp.Sanitize();
        _points = CorrectionCurve.Parse(_initial.CurvePoints).ToList();
        _ipc = App.Services.GetRequiredService<IpcService>();
        InitializeComponent();
        AutoHeadroom.IsChecked = _initial.AutoConvolutionHeadroom;
        TrimInput.Value = _initial.ConvolutionTrimDb;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(150); _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { if (_open) _ipc.PreviewDsp(Audition.IsOn ? Draft : _initial with { ConvolutionEnabled = false }); };
        Response.PointSelected += index => { _selected = index; SyncNodes(); };
        Response.PointMoved += MovePoint;
        Opened += async (_, _) =>
        {
            _open = true;
            ResizeEditor();
            XamlRoot.Changed += RootChanged;
            _ipc.DspStateChanged += PlaybackChanged;
            _syncing = false; SyncNodes(); PlaybackChanged(); EditChanged();
            await LoadPresets();
        };
        Closing += (_, args) =>
        {
            _timer.Stop(); _open = false;
            XamlRoot.Changed -= RootChanged;
            _ipc.DspStateChanged -= PlaybackChanged;
            // The caller commits only on Primary. Cancel/Escape restores the full previous DSP preference.
            _ipc.PreviewDsp(args.Result == ContentDialogResult.Primary ? Draft : AppSettings.Dsp);
        };
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => ResizeEditor();
    private void ResizeEditor()
    {
        EditorPanel.Width = Math.Clamp(XamlRoot.Size.Width - 100, 240, 800);
        EditorScroll.MaxHeight = Math.Max(180, XamlRoot.Size.Height - 190);
        bool narrow = EditorPanel.Width < 500;
        PointFields.ColumnDefinitions.Clear(); PointFields.RowDefinitions.Clear();
        for (int i = 0; i < (narrow ? 1 : 3); i++) PointFields.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < (narrow ? 3 : 1); i++) PointFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < PointFields.Children.Count; i++)
        {
            Grid.SetColumn((FrameworkElement)PointFields.Children[i], narrow ? 0 : i);
            Grid.SetRow((FrameworkElement)PointFields.Children[i], narrow ? i : 0);
        }
    }
    private void PlaybackChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_open) return;
        Audition.IsEnabled = _ipc.CurrentDspState?.State is { RenderKind: 0, IsEnabled: true, Channels: <= 2 };
    });
    private void SyncNodes()
    {
        _syncing = true;
        NodePicker.Items.Clear();
        for (int i = 0; i < _points.Count; i++) NodePicker.Items.Add($"{i + 1} · {_points[i].Frequency:0.#} Hz");
        _selected = Math.Clamp(_selected, 0, _points.Count - 1);
        NodePicker.SelectedIndex = _selected;
        FrequencyInput.Value = _points[_selected].Frequency; GainInput.Value = _points[_selected].GainDb;
        AddPointButton.IsEnabled = _points.Count < CorrectionCurve.MaxPoints;
        RemovePointButton.IsEnabled = _points.Count > 2;
        Response.SetPoints(_points.ToArray(), _selected);
        _syncing = false;
    }
    private void MovePoint(int index, double frequency, double gain)
    {
        double min = index == 0 ? 20 : _points[index - 1].Frequency + 0.05;
        double max = index == _points.Count - 1 ? 20000 : _points[index + 1].Frequency - 0.05;
        if (min > max) return;
        _points[index] = new(Math.Clamp(Math.Round(frequency, 2), min, max), Math.Round(Math.Clamp(gain, -12, 12), 2));
        _selected = index; SyncNodes(); EditChanged();
    }
    private void EditChanged()
    {
        if (_syncing || !_open) return;
        Notice.IsOpen = false; Response.Refresh(Draft); _timer.Stop(); _timer.Start();
    }
    private void NodeSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || NodePicker.SelectedIndex < 0) return;
        _selected = NodePicker.SelectedIndex; SyncNodes();
    }
    private void PointValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_syncing && double.IsFinite(FrequencyInput.Value) && double.IsFinite(GainInput.Value))
            MovePoint(_selected, FrequencyInput.Value, GainInput.Value);
    }
    private void AddPoint(object sender, RoutedEventArgs args)
    {
        if (_points.Count >= CorrectionCurve.MaxPoints) return;
        int left = 0;
        for (int i = 1; i < _points.Count - 1; i++)
            if (_points[i + 1].Frequency / _points[i].Frequency > _points[left + 1].Frequency / _points[left].Frequency) left = i;
        double hz = Math.Round(Math.Sqrt(_points[left].Frequency * _points[left + 1].Frequency), 2);
        if (hz - _points[left].Frequency < 0.02 || _points[left + 1].Frequency - hz < 0.02) return;
        _points.Insert(left + 1, new(hz, CorrectionCurve.Evaluate(_points.ToArray(), hz)));
        _selected = left + 1; SyncNodes(); EditChanged();
    }
    private void RemovePoint(object sender, RoutedEventArgs args)
    {
        if (_points.Count <= 2) return;
        _points.RemoveAt(_selected); SyncNodes(); EditChanged();
    }
    private void ResetFlat(object sender, RoutedEventArgs args)
    {
        _points.Clear(); _points.AddRange(CorrectionCurve.Parse(CorrectionCurve.Flat)); _selected = 0; SyncNodes(); EditChanged();
    }
    private void SettingsChanged(object sender, RoutedEventArgs args) => EditChanged();
    private void TrimChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => EditChanged();
    private void AuditionChanged(object sender, RoutedEventArgs args) { if (!_syncing) EditChanged(); }
    private async Task LoadPresets()
    {
        try
        {
            if (File.Exists(PresetPath))
            {
                if (new FileInfo(PresetPath).Length > 256 * 1024) throw new InvalidDataException();
                var presets = JsonSerializer.Deserialize(await File.ReadAllTextAsync(PresetPath), CurvePresetJsonContext.Default.ListCurvePreset) ?? [];
                if (presets.Count > 100) throw new InvalidDataException();
                foreach (var preset in presets)
                {
                    if (preset == null || string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Length > 80
                        || !double.IsFinite(preset.TrimDb) || preset.TrimDb is < -24 or > 0) throw new InvalidDataException();
                    CorrectionCurve.Parse(preset.Points);
                }
                _presets = presets;
            }
            if (_open) RebuildPresets();
        }
        catch { if (_open) Error("CurvePresetError"); }
    }
    private void RebuildPresets()
    {
        _syncing = true; Presets.Items.Clear();
        foreach (var preset in _presets) Presets.Items.Add(preset.Name);
        _syncing = false;
    }
    private void PresetSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || Presets.SelectedIndex < 0) return;
        var preset = _presets[Presets.SelectedIndex];
        _points.Clear(); _points.AddRange(CorrectionCurve.Parse(preset.Points));
        _syncing = true; AutoHeadroom.IsChecked = preset.AutoHeadroom; TrimInput.Value = preset.TrimDb; SyncNodes(); EditChanged();
    }
    private async void SavePreset(object sender, RoutedEventArgs args)
    {
        if (_saving) return;
        string name = PresetName.Text.Trim();
        if (name.Length == 0 || _presets.Count >= 100 || _presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        { Error("CurvePresetNameError"); return; }
        var draft = Draft;
        var updated = new List<CurvePreset>(_presets) { new(name, draft.CurvePoints, draft.AutoConvolutionHeadroom, draft.ConvolutionTrimDb) };
        try
        {
            _saving = true;
            string json = JsonSerializer.Serialize(updated, CurvePresetJsonContext.Default.ListCurvePreset);
            await File.WriteAllTextAsync(PresetPath + ".tmp", json);
            File.Move(PresetPath + ".tmp", PresetPath, true);
            _presets = updated; RebuildPresets(); SaveFlyout.Hide();
        }
        catch { Error("CurvePresetError"); }
        finally { _saving = false; }
    }
    private void Error(string key) { Notice.Message = ToolUtils.GetString(key); Notice.IsOpen = true; }
}
