using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public sealed class AudioSettingsSynchronizer(BassPlayerCommandService player) : IDisposable
{
    private bool _started;
    public void Start()
    {
        if (_started) return;
        _started = true;
        AppSettings.OutputSettingsChanged += OutputChanged;
        AppSettings.OutputSettingsUpdated += OutputUpdated;
        AppSettings.EqUpdated += EqUpdated;
    }
    private void OutputChanged(object? sender, EventArgs e) => player.ChangingSetting();
    private void OutputUpdated(object? sender, EventArgs e) => player.UpdateSettings();
    private void EqUpdated(object? sender, EventArgs e) => player.EqUpdate();
    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        AppSettings.OutputSettingsChanged -= OutputChanged;
        AppSettings.OutputSettingsUpdated -= OutputUpdated;
        AppSettings.EqUpdated -= EqUpdated;
    }
}
