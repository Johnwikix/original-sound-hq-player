using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassPlayerIpc.Shared;

/// <summary>保存不可变全局音频快照；无变更不写盘，读取失败禁止覆盖原文件。</summary>
public sealed class AudioSettingsStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AudioPreferences? _current;
    private long _revision;

    /// <summary>只有新文件不存在时才导入旧字段；存在但损坏或版本未知的文件不重新迁移。</summary>
    public async Task<AudioPreferences> LoadAsync(AudioPreferences legacy)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _current = null;
            AudioSettingsDocument document;
            if (File.Exists(path))
            {
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                document = JsonSerializer.Deserialize(bytes, AudioSettingsJsonContext.Default.AudioSettingsDocument)
                    ?? throw new InvalidDataException("Empty audio settings.");
                if (document.SchemaVersion != 1) throw new NotSupportedException("Unsupported audio settings schema.");
                if (document.Revision < 1 || document.Preferences == null) throw new InvalidDataException("Invalid audio settings.");
                document = document with { Preferences = document.Preferences.Sanitize() };
            }
            else
            {
                document = new() { Revision = 1, Preferences = legacy.Sanitize() };
                await WriteAsync(document).ConfigureAwait(false);
            }
            _current = document.Preferences;
            _revision = document.Revision;
            return _current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>成功替换文件后才推进已提交快照与版本。</summary>
    public async Task SaveAsync(AudioPreferences candidate)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_current == null) throw new InvalidOperationException("Audio settings have not loaded successfully.");
            if (candidate == _current) return;
            var snapshot = candidate.Sanitize();
            if (snapshot == _current) return;
            long revision = checked(_revision + 1);
            await WriteAsync(new() { Revision = revision, Preferences = snapshot }).ConfigureAwait(false);
            _current = snapshot;
            _revision = revision;
        }
        finally { _gate.Release(); }
    }

    private Task WriteAsync(AudioSettingsDocument document) => AtomicSettingsFile.WriteAsync(path,
        JsonSerializer.SerializeToUtf8Bytes(document, AudioSettingsJsonContext.Default.AudioSettingsDocument), keepBackup: false);
}

public sealed record AudioSettingsDocument
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public long Revision { get; init; }
    [JsonRequired]
    public AudioPreferences Preferences { get; init; } = new();
}

[JsonSerializable(typeof(AudioSettingsDocument))]
public partial class AudioSettingsJsonContext : JsonSerializerContext;
