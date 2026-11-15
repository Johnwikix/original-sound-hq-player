using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// USB 存储设备生命周期唯一归属：设备发现（DeviceWatcher）、设备列表、当前选中设备、
    /// 设备上的音乐记录（发送台账）与按设备扫描全部在此收敛。
    /// 界面（下拉框/菜单）与发送流程只消费本服务的状态与事件，不再各自持有全局状态——
    /// 取代原先散落在 AppData 静态属性、MusicBrowseViewModel、MusicBrowsePage 代码后置中的三份状态。
    /// </summary>
    public class UsbDeviceService : ObservableObject
    {
        private readonly MusicDatabaseService _musicDatabaseService;
        private readonly ILogger<UsbDeviceService> _logger;
        private DeviceWatcher? _deviceWatcher;
        private volatile bool _stopped;
        private CancellationTokenSource? _scanCts;
        private readonly CancellationTokenSource _watchCts = new();
        private readonly SemaphoreSlim _refreshGate = new(1, 1);
        private int _refreshVersion;
        private readonly List<Task> _selectionTasks = [];
        private readonly SemaphoreSlim _scanGate = new(1, 1);
        private readonly Dictionary<string, int> _musicIndices = new(StringComparer.OrdinalIgnoreCase);
        private ScanSession? _session;
        private int _exportsInProgress;
        private volatile bool _refreshAfterExport;

        private sealed class ExportPause(UsbDeviceService owner) : IDisposable
        {
            private UsbDeviceService? _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ResumeScanAfterExport();
        }

        private sealed class ScanSession(UsbStorageDevice device, CancellationToken stopToken) : IDisposable
        {
            internal UsbStorageDevice Device { get; } = device;
            internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            internal SemaphoreSlim Wake { get; } = new(0, 1);
            internal bool Pending { get; set; } = true;
            internal void Signal()
            {
                try { Wake.Release(); }
                catch (SemaphoreFullException) { } // 合并连接状态更新和发送完成的唤醒。
            }
            public void Dispose()
            {
                Cancellation.Dispose();
                Wake.Dispose();
            }
        }

        /// <summary>当前枚举到的 USB 存储设备（下拉框数据源）。</summary>
        public ObservableCollection<UsbStorageDevice> Devices { get; } = [];

        /// <summary>当前选中的设备；null 表示未选择。TwoWay 绑定到设备下拉框，
        /// 赋值即触发台账加载与扫描（自动选中与手动选择走同一入口）。</summary>
        public UsbStorageDevice? SelectedDevice
        {
            get => field;
            set
            {
                if (_stopped || ReferenceEquals(field, value)) return;
                SetProperty(ref field, value);
                var previous = _session;
                _session = value is null ? null : new ScanSession(value, _watchCts.Token);
                previous?.Cancellation.Cancel();
                MusicOnDevice.Clear();
                _musicIndices.Clear();
                RaiseDeviceMusicChanged();
                _selectionTasks.RemoveAll(static task => task.IsCompleted);
                if (_session is { } session) _selectionTasks.Add(SelectCoreAsync(session));
            }
        }

        /// <summary>选中设备的音乐发送台账（含历史扫描与本次会话的发送记录）。</summary>
        public ObservableCollection<UsbDeviceMusic> MusicOnDevice { get; } = [];

        private Visibility _devicesVisibility = Visibility.Collapsed;
        /// <summary>设备下拉框的可见性（无设备时隐藏）。</summary>
        public Visibility DevicesVisibility { get => _devicesVisibility; private set => SetProperty(ref _devicesVisibility, value); }

        /// <summary>设备列表变化（插入/移除/枚举完成）→ 右键菜单同步。</summary>
        public event EventHandler? DevicesChanged;
        /// <summary>发送台账变化 → 依设备标记（IsExistOnDevice）刷新。</summary>
        public event EventHandler<UsbDeviceMusicChangedEventArgs>? DeviceMusicChanged;

        public UsbDeviceService(MusicDatabaseService musicDatabaseService, ILogger<UsbDeviceService> logger)
        {
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
        }

        public void StartWatching()
        {
            if (_deviceWatcher is not null || _stopped) return;
            try
            {
                string deviceSelector = StorageDevice.GetDeviceSelector();
                _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
                _deviceWatcher.Added += OnDeviceAdded;
                _deviceWatcher.Removed += OnDeviceChanged;
                _deviceWatcher.Updated += OnDeviceChanged;
                _deviceWatcher.EnumerationCompleted += OnEnumerationCompleted;
                _deviceWatcher.Start();
                _ = RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"启动 USB 设备监视器失败: {ex.Message}");
            }
        }

        public void StopWatching()
        {
            if (_stopped) return;
            _stopped = true;
            _watchCts.Cancel();
            if (_deviceWatcher is not null)
            {
                _deviceWatcher.Added -= OnDeviceAdded;
                _deviceWatcher.Removed -= OnDeviceChanged;
                _deviceWatcher.Updated -= OnDeviceChanged;
                _deviceWatcher.EnumerationCompleted -= OnEnumerationCompleted;
            }
            if (_deviceWatcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _deviceWatcher.Stop();
            _deviceWatcher = null;
            _scanCts?.Cancel();
        }

        public async Task StopAsync()
        {
            StopWatching();
            await Task.WhenAll(_selectionTasks);
            // 枚举无法中断；等待实际调用返回后再释放数据库和 UI 依赖。
            await _refreshGate.WaitAsync();
            _refreshGate.Release();
        }

        private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation args)
        {
            try
            {
                await Task.Delay(1500, _watchCts.Token); // 等待手机公开内部存储
                await RefreshDevicesAsync();
            }
            catch (OperationCanceledException) when (_stopped) { }
        }

        private async void OnDeviceChanged(DeviceWatcher sender, DeviceInformationUpdate args) => await RefreshDevicesAsync();

        private async void OnEnumerationCompleted(DeviceWatcher sender, object args) => await RefreshDevicesAsync();

        /// <summary>重新枚举 USB 存储设备并更新设备列表/可见性；
        /// 无选中或选中设备被移除时自动选中第一项，免去插拔后的手动选择。</summary>
        public async Task RefreshDevicesAsync()
        {
            int version = Interlocked.Increment(ref _refreshVersion);
            bool entered = false;
            try
            {
                if (_stopped) return;
                if (Volatile.Read(ref _exportsInProgress) > 0)
                {
                    _refreshAfterExport = true;
                    return;
                }
                await _refreshGate.WaitAsync(_watchCts.Token);
                entered = true;
                if (_stopped || version != Volatile.Read(ref _refreshVersion)) return;
                var devices = await Task.Run(UsbStorageDeviceReader.GetUsbStorageDevicesAsync);
                if (_stopped || version != Volatile.Read(ref _refreshVersion)) return;
                var dq = App.MainWindow.DispatcherQueue;
                await dq.EnqueueAsync(() =>
                {
                    if (_stopped || version != Volatile.Read(ref _refreshVersion)) return;
                    // 保留相同 ID 的对象，避免刷新清空 ItemsSource 时 TwoWay 绑定撤销选择。
                    for (int i = Devices.Count - 1; i >= 0; i--)
                        if (!devices.Exists(d => d.UniqueId == Devices[i].UniqueId)) Devices.RemoveAt(i);
                    foreach (var device in devices)
                        if (Devices.AsValueEnumerable().All(d => d.UniqueId != device.UniqueId)) Devices.Add(device);
                    DevicesVisibility = Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (Devices.Count == 0)
                    {
                        ClearSelection();
                        DevicesChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    // 自动选中：当前无选中、或选中的设备已被拔出（按 UniqueId 判断）
                    if (SelectedDevice is null || Devices.AsValueEnumerable().All(d => d.UniqueId != SelectedDevice.UniqueId))
                        SelectedDevice = Devices[0];
                    // 相同设备对象不再触发选择 setter；未完成的扫描需要独立唤醒。
                    if (_session is { Pending: true } pending) pending.Signal();
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                });
            }
            catch (OperationCanceledException) when (_stopped) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"枚举 USB 设备失败: {ex.Message}");
            }
            finally
            {
                if (entered) _refreshGate.Release();
            }
        }

        /// <summary>先发布缓存，再以单工作循环核对目录；取消或失败不会冒充扫描完成。</summary>
        private async Task SelectCoreAsync(ScanSession session)
        {
            var token = session.Cancellation.Token;
            try
            {
                try { await LoadDeviceMusicAsync(session, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, "加载 USB 缓存失败，将在扫描时重试: {Device}", session.Device.Name); }
                int failures = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (!session.Pending || Volatile.Read(ref _exportsInProgress) > 0)
                    {
                        await session.Wake.WaitAsync(token);
                        continue;
                    }
                    await _scanGate.WaitAsync(token);
                    bool interrupted = false;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref _exportsInProgress) > 0) continue;
                        using var scan = CancellationTokenSource.CreateLinkedTokenSource(token);
                        _scanCts = scan;
                        try
                        {
                            var ct = scan.Token;
                            // 上一批可能已入库但因抢占尚未发布；重试从已提交状态补齐 UI。
                            await LoadDeviceMusicAsync(session, ct, overwrite: true);
                            ct.ThrowIfCancellationRequested();
                            bool complete = await Task.Run(() => _musicDatabaseService.ScanUsbDeviceAsync(session.Device, ct,
                                records => PublishScanBatchAsync(session, records, ct),
                                removed => PublishScanRemovalsAsync(session, removed, ct)), ct);
                            ct.ThrowIfCancellationRequested();
                            session.Pending = !complete;
                            failures = complete ? 0 : failures + 1;
                        }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        {
                            // 发送抢占了扫描；发送完成会唤醒此会话，继续核对尚未完成的部分。
                            session.Pending = true;
                            interrupted = true;
                        }
                        finally
                        {
                            if (ReferenceEquals(_scanCts, scan)) _scanCts = null;
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        session.Pending = true;
                        failures++;
                        _logger.LogWarning(ex, "USB 扫描未完成，将自动补扫: {Device}", session.Device.Name);
                    }
                    finally { _scanGate.Release(); }
                    if (session.Pending && !interrupted && Volatile.Read(ref _exportsInProgress) == 0)
                    {
                        // 失败重试逐渐退避到两分钟；状态更新可以提前唤醒，但至少间隔两秒。
                        double seconds = Math.Min(120, 5 * Math.Pow(2, Math.Min(failures - 1, 5)));
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        await session.Wake.WaitAsync(TimeSpan.FromSeconds(Math.Max(0, seconds - 2)), token);
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_session, session)) _session = null;
                session.Dispose();
            }
        }

        /// <summary>清空选中与台账（设备全部移除时）。选中置 null 会经由属性触发台账清理。</summary>
        public void ClearSelection()
        {
            _scanCts?.Cancel();
            SelectedDevice = null;
        }

        /// <summary>发送期间暂停扫描；缓存读取继续，释放租约后自动补扫。入口和释放均由 UI 线程调用。</summary>
        public IDisposable PauseScanForExport()
        {
            Interlocked.Increment(ref _exportsInProgress);
            if (_session is { } session) session.Pending = true;
            _scanCts?.Cancel();
            return new ExportPause(this);
        }

        private void ResumeScanAfterExport()
        {
            if (Interlocked.Decrement(ref _exportsInProgress) != 0 || _stopped) return;
            _session?.Signal();
            if (_refreshAfterExport)
            {
                _refreshAfterExport = false;
                _ = RefreshDevicesAsync();
            }
        }

        /// <summary>每首音频落盘后发布状态；同路径覆盖，同标题的不同文件均保留。</summary>
        public void AddSentRecords(IReadOnlyList<UsbDeviceMusic> sent, UsbStorageDevice device)
        {
            if (sent.Count == 0 || _stopped || SelectedDevice?.UniqueId != device.UniqueId) return;
            ApplyRecords(sent);
        }

        private void ApplyRecords(IReadOnlyList<UsbDeviceMusic> records, bool overwrite = true)
        {
            var changedTitles = new HashSet<string>(StringComparer.Ordinal);
            var changedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                if (_musicIndices.TryGetValue(record.Path, out int index))
                {
                    if (!overwrite) continue; // 缓存回读不能覆盖同时到达的新发送记录。
                    var previous = MusicOnDevice[index];
                    bool sameDisplay = previous.Title == record.Title && previous.Author == record.Author
                        && previous.Album == record.Album && previous.Extension == record.Extension
                        && (previous.ScanMetadataRead == false) == (record.ScanMetadataRead == false);
                    if (sameDisplay)
                    {
                        // 指纹和数据库身份不参与绑定，避免每次缓存核对产生整库 Replace 通知。
                        previous.Id = record.Id;
                        previous.ScanFileSize = record.ScanFileSize;
                        previous.ScanModifiedTicks = record.ScanModifiedTicks;
                        previous.ScanMetadataRead = record.ScanMetadataRead;
                        continue;
                    }
                    MusicOnDevice[index] = record;
                    changedTitles.Add(previous.Title);
                    changedFiles.Add(Path.GetFileNameWithoutExtension(previous.Path));
                }
                else
                {
                    _musicIndices[record.Path] = MusicOnDevice.Count;
                    MusicOnDevice.Add(record);
                }
                changedTitles.Add(record.Title);
                changedFiles.Add(Path.GetFileNameWithoutExtension(record.Path));
            }
            if (changedTitles.Count > 0) RaiseDeviceMusicChanged(changedTitles, changedFiles);
        }

        private async Task LoadDeviceMusicAsync(ScanSession session, CancellationToken token, bool overwrite = false)
        {
            var records = await _musicDatabaseService.GetUsbDeviceMusics(session.Device.UniqueId) ?? [];
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (!CanPublish(session, token)) return;
                ApplyRecords(records, overwrite);
                if (overwrite)
                {
                    var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var record in records) present.Add(record.Path);
                    var removed = new List<string>();
                    foreach (var record in MusicOnDevice)
                        if (!present.Contains(record.Path)) removed.Add(record.Path);
                    RemoveRecords(removed);
                }
            });
        }

        private bool CanPublish(ScanSession session, CancellationToken token)
            => !_stopped && !token.IsCancellationRequested && ReferenceEquals(_session, session);

        private Task PublishScanBatchAsync(ScanSession session, IReadOnlyList<UsbDeviceMusic> records, CancellationToken token)
            => App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (CanPublish(session, token)) ApplyRecords(records);
            });

        private Task PublishScanRemovalsAsync(ScanSession session, IReadOnlyList<string> removed, CancellationToken token)
            => App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (CanPublish(session, token)) RemoveRecords(removed);
            });

        private void RemoveRecords(IReadOnlyList<string> removed)
        {
            if (removed.Count == 0) return;
            var paths = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = MusicOnDevice.Count - 1; i >= 0; i--)
            {
                var record = MusicOnDevice[i];
                if (!paths.Contains(record.Path)) continue;
                titles.Add(record.Title);
                files.Add(Path.GetFileNameWithoutExtension(record.Path));
                MusicOnDevice.RemoveAt(i);
            }
            _musicIndices.Clear();
            for (int i = 0; i < MusicOnDevice.Count; i++) _musicIndices[MusicOnDevice[i].Path] = i;
            if (titles.Count > 0) RaiseDeviceMusicChanged(titles, files);
        }

        private void RaiseDeviceMusicChanged(IReadOnlyCollection<string>? changedTitles = null,
            IReadOnlyCollection<string>? changedFiles = null)
            => DeviceMusicChanged?.Invoke(this, new UsbDeviceMusicChangedEventArgs(changedTitles, changedFiles));
    }

    public sealed class UsbDeviceMusicChangedEventArgs(IReadOnlyCollection<string>? changedTitles,
        IReadOnlyCollection<string>? changedFileNames = null) : EventArgs
    {
        public IReadOnlyCollection<string>? ChangedTitles { get; } = changedTitles;
        public IReadOnlyCollection<string>? ChangedFileNames { get; } = changedFileNames;
    }
}
