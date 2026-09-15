using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace BassPlayerIpc.Shared;

/// <summary>设置文件统一采用无备份原子提交；确认损坏后隔离并重建默认值。</summary>
public static class AtomicSettingsFile
{
    public static async Task WriteAsync(string path, byte[] bytes)
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
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            // 清理失败不能掩盖原始写入异常；临时文件不作为配置读取。
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>源生成 JSON 读取；仅缺失时迁移，损坏时使用默认值，未知版本或 IO 失败不覆盖。</summary>
    public static async Task<T> LoadAsync<T>(string path, JsonTypeInfo<T> jsonType,
        Func<T> missing, Func<T> defaults, Func<T, T>? validate = null,
        int? schemaVersion = null, Action<string>? onReset = null) where T : class
    {
        byte[] bytes;
        try { bytes = await ReadBytesAsync(path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            T initial = missing();
            if (validate != null) initial = validate(initial);
            await WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(initial, jsonType)).ConfigureAwait(false);
            return initial;
        }

        try
        {
            if (schemaVersion is { } supported)
            {
                // 先检查版本；未来版本即使缺少本版本必填字段也不能按损坏重置。
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("SchemaVersion", out var version)
                    && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int actual)
                    && actual != supported)
                    throw new NotSupportedException("Unsupported settings schema.");
            }
            T result = JsonSerializer.Deserialize(bytes, jsonType) ?? throw new InvalidDataException("Empty settings.");
            return validate == null ? result : validate(result);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            T fallback = defaults();
            if (validate != null) fallback = validate(fallback);
            byte[] replacement = JsonSerializer.SerializeToUtf8Bytes(fallback, jsonType);
            string preserved = path + ".corrupt-" + Guid.NewGuid().ToString("N");
            // 先复制诊断文件，再原子提交默认值；重建失败仍保留主文件，禁止错误重迁移。
            File.Copy(path, preserved);
            await WriteAsync(path, replacement).ConfigureAwait(false);
            onReset?.Invoke(preserved);
            return fallback;
        }
    }

    private static async Task<byte[]> ReadBytesAsync(string path)
    {
        try { return await File.ReadAllBytesAsync(path).ConfigureAwait(false); }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            // Windows 共享/锁冲突只短暂重试一次；权限错误和其他 IO 错误不视为损坏。
            await Task.Delay(100).ConfigureAwait(false);
            return await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
    }
}
