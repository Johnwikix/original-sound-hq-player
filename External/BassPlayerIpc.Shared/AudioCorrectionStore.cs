using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassPlayerIpc.Shared;

/// <summary>同目录临时文件替换；失败保留上次提交，调用方显式选择是否保留备份。</summary>
public static class AtomicSettingsFile
{
    public static async Task WriteAsync(string path, byte[] bytes, bool keepBackup = true)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, keepBackup ? path + ".bak" : null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>独立设备配置仓库；只在首次不存在文件时迁移旧配置。</summary>
public sealed class AudioCorrectionStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private long _revision;

    public async Task<DeviceCorrections> LoadAsync(DeviceCorrections? legacy)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _loaded = false;
            AudioCorrectionDocument document;
            if (!File.Exists(path) && !File.Exists(path + ".bak"))
            {
                document = new() { Revision = 1, Corrections = (legacy ?? new()).Validate() };
                await WriteAsync(document).ConfigureAwait(false);
            }
            else
            {
                try { document = await ReadAsync(path).ConfigureAwait(false); }
                catch (Exception ex) when ((ex is JsonException or InvalidDataException or FileNotFoundException or ArgumentException) && File.Exists(path + ".bak"))
                {
                    document = await ReadAsync(path + ".bak").ConfigureAwait(false);
                    // 仅兼容上一版留下的备份：恢复后即转为无备份的原子保存。
                    if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    await WriteAsync(document).ConfigureAwait(false);
                }
            }
            TryRemoveLegacyBackup();
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
            TryRemoveLegacyBackup();
            return snapshot;
        }
        finally { _gate.Release(); }
    }

    private Task WriteAsync(AudioCorrectionDocument document) => AtomicSettingsFile.WriteAsync(path,
        JsonSerializer.SerializeToUtf8Bytes(document, AudioCorrectionJsonContext.Default.AudioCorrectionDocument), keepBackup: false);

    private void TryRemoveLegacyBackup()
    {
        // 主文件已经成功读取或提交；旧备份不再是数据源。被其他程序占用时留待下次清理。
        try { File.Delete(path + ".bak"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<AudioCorrectionDocument> ReadAsync(string source)
    {
        var bytes = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize(bytes, AudioCorrectionJsonContext.Default.AudioCorrectionDocument)
            ?? throw new InvalidDataException("Empty correction configuration.");
        if (document.SchemaVersion != 1) throw new NotSupportedException("Unsupported correction schema.");
        if (document.Revision < 1 || document.Corrections == null) throw new InvalidDataException("Invalid correction configuration.");
        return document with { Corrections = document.Corrections.Validate() };
    }
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
