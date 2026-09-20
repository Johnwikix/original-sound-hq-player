using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using System;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public sealed class FrequencyResponseViewModel : ObservableObject
{
    public sealed record Preview(double[] Frequencies, double[] Eq, double[][] Fir, double[][] Combined, int Rate, double AutoGain);
    private Preview? _preview;
    public Preview? Current => _preview;
    private CancellationTokenSource? _cancellation;
    private bool _loaded;
    private Task _refreshTask = Task.CompletedTask;
    private int _rate = 48000;
    private string? _cacheKey;
    private ImpulseResponse? _cacheImpulse;
    private Complex[][]? _cacheSpectra;
    private readonly DispatcherQueue _queue = DispatcherQueue.GetForCurrentThread();
    public string Status { get => field; private set => SetProperty(ref field, value); } = "";
    public event Action? Updated;
    public void Load()
    {
        if (_loaded) return;
        _loaded = true;
        App.Services.GetRequiredService<IpcService>().DspStateChanged += StateChanged;
        App.Services.GetRequiredService<LicenseService>().StateChanged += StateChanged;
        AppSettings.EqUpdated += EqChanged;
        AppSettings.AudioResponseChanged += EqChanged;
        Refresh();
    }
    public void Unload()
    {
        _loaded = false;
        App.Services.GetRequiredService<IpcService>().DspStateChanged -= StateChanged;
        App.Services.GetRequiredService<LicenseService>().StateChanged -= StateChanged;
        AppSettings.EqUpdated -= EqChanged;
        AppSettings.AudioResponseChanged -= EqChanged;
        _cancellation?.Cancel();
    }
    private void EqChanged(object? sender, EventArgs e) => StateChanged();
    private void StateChanged() => _queue.TryEnqueue(() => { if (_loaded) Refresh(); });
    public async Task StopAsync()
    {
        Unload();
        await _refreshTask;
        if (_loaded) return;
        _preview = null;
        _cacheKey = null;
        _cacheImpulse = null;
        _cacheSpectra = null;
    }
    public void Refresh()
    {
        if (!_loaded) return;
        _cancellation?.Cancel();
        _refreshTask = RefreshCoreAsync(_refreshTask);
    }
    private async Task RefreshCoreAsync(Task previous)
    {
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var token = cancellation.Token;
        var state = App.Services.GetRequiredService<IpcService>().CurrentDspState?.State;
        int rate = state is { SampleRate: >= 8000 } value ? value.SampleRate : 48000;
        _rate = rate;
        // Apply the playback policy after resolving saved/device/live corrections.
        var dsp = LicensePolicy.ApplyDspRestrictions(
            AppSettings.ResolveResponseSettings(state?.OutputDeviceId, state?.OutputGeneration ?? 0),
            App.Services.GetRequiredService<LicenseService>().RestrictedFeatures);
        bool eqEnabled = AppSettings.IsEqualizerEnabled;
        var eq = AppSettings.EqualizerBands.Select(b => PeakCoefficients.Create(b.FrequencyHz, (float)b.GainDb, (float)b.Q, rate)).ToArray();
        string key = rate + ":" + (CorrectionCurve.UsesCurve(dsp) ? "curve:" + dsp.CurvePoints : "wave:" + dsp.ImpulsePath);
        var cachedImpulse = key == _cacheKey ? _cacheImpulse : null;
        var cachedSpectra = key == _cacheKey ? _cacheSpectra : null;
        Status = ToolUtils.GetString("ResponsePreparing");
        try
        {
            await previous;
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
                double automatic = dsp.ConvolutionEnabled && !dsp.AutoPreamp.HasValue && dsp.AutoConvolutionHeadroom && CorrectionCurve.UsesCurve(dsp) && spectra != null
                    ? -20 * Math.Log10(Math.Max(1, spectra.SelectMany(x => x.Take(x.Length / 2 + 1)).Max(x => x.Magnitude))) : 0;
                double preamp = dsp.AutoPreamp == true ? ResponseMath.AutoPreampDb(eqEnabled ? eq : [], dsp.ConvolutionEnabled ? spectra : null, rate) : dsp.HeadroomDb;
                double trim = dsp.AutoPreamp.HasValue ? 0 : dsp.ConvolutionTrimDb + automatic;
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
                            ? ResponseMath.MagnitudeDb(spectra[Math.Min(ch, spectra.Length - 1)], rate, hz) + trim : 0;
                        combined[ch][i] = equalizer[i] + fir[ch][i] + preamp;
                    }
                }
                return (Preview: new Preview(frequencies, equalizer, fir, combined, rate, dsp.AutoPreamp == true ? preamp : (dsp.AutoPreamp.HasValue ? preamp : automatic)), Impulse: impulse, Spectra: spectra);
            }, token);
            if (token.IsCancellationRequested || !_loaded) return;
            _preview = result.Preview;

            _cacheKey = key; _cacheImpulse = result.Impulse; _cacheSpectra = result.Spectra;
            bool bypass = state == null || state.Value.SampleRate <= 0 || state.Value.RenderKind != 0 || !state.Value.IsEnabled || state.Value.Channels > 2;
            Status = string.Format(ToolUtils.GetString(bypass ? "ResponseBypass" : "ResponseReady"), rate / 1000.0, result.Preview.AutoGain);
            Updated?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (token.IsCancellationRequested || !_loaded) return;
            _preview = null; Updated?.Invoke(); Status = ToolUtils.GetString("ResponseFailed");
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
        }
    }

}
