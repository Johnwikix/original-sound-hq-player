using System.IO.MemoryMappedFiles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BassPlayerIpc.Shared;

/// <summary>设备校正快照；仅覆盖卷积参数，其他音效沿用全局设置。</summary>
public sealed record DeviceCorrection
{
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public CorrectionSettings Settings { get; init; } = new();

    public DspSettings Apply(DspSettings global) => global with
    {
        ConvolutionEnabled = global.ConvolutionEnabled,
        ConvolutionSource = Settings.ConvolutionSource,
        CurvePoints = Settings.CurvePoints,
        CurvePresetName = Settings.CurvePresetName,
        ImpulsePath = Settings.ImpulsePath,
        ConvolutionTrimDb = Settings.ConvolutionTrimDb,
        AutoConvolutionHeadroom = Settings.AutoConvolutionHeadroom
    };
}

/// <summary>按稳定端点 ID（ASIO 使用驱动 CLSID）匹配，未知设备旁路卷积。</summary>
public sealed record DeviceCorrections
{
    public bool Enabled { get; init; }
    public DeviceCorrection[] Bindings { get; init; } = [];

    public DeviceCorrection? Find(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return null;
        // 控制线程查找；音频回调不查表、不分配。项目使用 .NET 11。
        foreach (var binding in Bindings)
            if (string.Equals(binding.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) return binding;
        return null;
    }

    public DspSettings Resolve(DspSettings global, string? deviceId) => !Enabled ? global
        : Find(deviceId) is { } binding ? binding.Apply(global)
        : global with { ConvolutionEnabled = false };

    public DeviceCorrections Validate()
    {
        if (Bindings == null || Bindings.Length > 64) throw new ArgumentException("Too many device corrections.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = new DeviceCorrection[Bindings.Length];
        for (int i = 0; i < Bindings.Length; i++)
        {
            var binding = Bindings[i];
            if (binding == null || string.IsNullOrWhiteSpace(binding.DeviceId)
                || System.Text.Encoding.UTF8.GetByteCount(binding.DeviceId) > 256
                || binding.DeviceId.Contains('\0') || binding.DeviceName == null || binding.DeviceName.Length > 256
                || !ids.Add(binding.DeviceId) || binding.Settings == null)
                throw new ArgumentException("Invalid device correction.");
            clean[i] = binding with { Settings = binding.Settings.ToUnifiedGain() };
        }
        return this with { Bindings = clean };
    }
}

/// <summary>独立配置邮箱：避免设备集合超出 2 KB 命令槽；完整快照在互斥锁内发布。</summary>
public sealed class DeviceCorrectionMailbox : IDisposable
{
    public const string Name = "AudioPlayer_DeviceCorrections_v1";
    private const int Capacity = 256 * 1024;
    private readonly MemoryMappedFile _memory;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex _mutex;

    public DeviceCorrectionMailbox(string name = Name)
    {
        _memory = MemoryMappedFile.CreateOrOpen(name, Capacity + sizeof(int));
        _view = _memory.CreateViewAccessor();
        _mutex = new Mutex(false, name + "_Lock");
    }

    private void Enter()
    {
        try { if (!_mutex.WaitOne(1000)) throw new TimeoutException("Device correction mailbox timed out."); }
        catch (AbandonedMutexException)
        {
            _view.Write(0, 0);
            _mutex.ReleaseMutex();
            throw new InvalidOperationException("Device correction writer exited.");
        }
    }

    public void Publish(DeviceCorrections settings)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(settings.Validate(), DeviceCorrectionJsonContext.Default.DeviceCorrections);
        if (bytes.Length > Capacity) throw new ArgumentException("Device corrections exceed mailbox capacity.");
        Enter();
        try
        {
            _view.Write(0, 0);
            _view.WriteArray(sizeof(int), bytes, 0, bytes.Length);
            _view.Write(0, bytes.Length);
        }
        finally { _mutex.ReleaseMutex(); }
    }

    public DeviceCorrections Read()
    {
        byte[] bytes;
        Enter();
        try
        {
            int length = _view.ReadInt32(0);
            if (length <= 0 || length > Capacity) throw new InvalidDataException("Invalid correction mailbox.");
            bytes = new byte[length];
            _view.ReadArray(sizeof(int), bytes, 0, length);
        }
        finally { _mutex.ReleaseMutex(); }
        return JsonSerializer.Deserialize(bytes, DeviceCorrectionJsonContext.Default.DeviceCorrections)
            ?? throw new InvalidDataException();
    }

    public void Dispose() { _mutex.Dispose(); _view.Dispose(); _memory.Dispose(); }
}

[JsonSerializable(typeof(DeviceCorrections))]
public partial class DeviceCorrectionJsonContext : JsonSerializerContext;
