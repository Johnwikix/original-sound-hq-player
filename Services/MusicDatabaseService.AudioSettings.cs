using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    private AudioSettingsStore? _audioSettingsStore;
    private bool _audioSettingsMigrated;

    private async Task<AudioPreferences> LoadAudioPreferencesAsync(SaveSettings settings)
    {
        string path = Path.Combine(Path.GetDirectoryName(SettingsPath)!, "AudioSettings.json");
        var legacy = settings.ReadLegacyAudioPreferences();
        _audioSettingsStore = new AudioSettingsStore(path);
        _audioSettingsMigrated = false;
        try
        {
            if (_settingsReadFailed && !File.Exists(path))
                throw new IOException("Cannot migrate audio preferences from unreadable settings.");
            var audio = await Task.Run(() => _audioSettingsStore.LoadAsync(legacy));
            if (_audioSettingsStore.ResetToDefaults)
                _logger.LogWarning("AudioSettings.json 已损坏，已隔离原文件并恢复默认音频设置");
            _audioSettingsMigrated = true;
            return audio;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioSettings.json could not be loaded; audio writes remain disabled.");
            // 首次迁移写入失败时仍沿用旧偏好；故障文件保持只读，不能把回退值写回。
            try { return legacy.Sanitize(); }
            catch (ArgumentException) { return new AudioPreferences(); }
        }
    }

}
