using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassPlayerIpc.Shared;

/// <summary>独立设备配置仓库；只在首次不存在文件时迁移旧配置。</summary>
public sealed class AudioCorrectionStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private long _revision;
    public bool ResetToDefaults { get; private set; }

    public async Task<DeviceCorrections> LoadAsync(DeviceCorrections? legacy)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _loaded = false;
            ResetToDefaults = false;
            var document = await AtomicSettingsFile.LoadAsync(path, AudioCorrectionJsonContext.Default.AudioCorrectionDocument,
                () => new() { Revision = 1, Corrections = legacy ?? new() },
                static () => new() { Revision = 1, Corrections = new() },
                static document =>
                {
                    if (document.SchemaVersion != 1) throw new NotSupportedException("Unsupported correction schema.");
                    if (document.Revision < 1 || document.Corrections == null) throw new InvalidDataException("Invalid correction configuration.");
                    return document with { Corrections = document.Corrections.Validate() };
                }, schemaVersion: 1, onReset: _ => ResetToDefaults = true).ConfigureAwait(false);
            _revision = document.Revision;
            _loaded = true;
            return document.Corrections;
        }
        finally { _gate.Release(); }
    }

    public async Task<DeviceCorrections> SaveAsync(DeviceCorrections candidate)
    {
        var snapshot = candidate.Validate();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_loaded) throw new InvalidOperationException("Correction configuration has not loaded successfully.");
            await WriteAsync(new() { Revision = checked(_revision + 1), Corrections = snapshot }).ConfigureAwait(false);
            ++_revision;
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private Task WriteAsync(AudioCorrectionDocument document) => AtomicSettingsFile.WriteAsync(path,
        JsonSerializer.SerializeToUtf8Bytes(document, AudioCorrectionJsonContext.Default.AudioCorrectionDocument));
}

public sealed record AudioCorrectionDocument
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = 1;
    [JsonRequired]
    public long Revision { get; init; }
    [JsonRequired]
    public DeviceCorrections Corrections { get; init; } = new();
}

[JsonSerializable(typeof(AudioCorrectionDocument))]
internal partial class AudioCorrectionJsonContext : JsonSerializerContext;
