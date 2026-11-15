using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        /// <summary>当前枚举到的 USB 存储设备（下拉框数据源）。</summary>
        public ObservableCollection<UsbStorageDevice> Devices { get; } = [];

        /// <summary>当前选中的设备；null 表示未选择。TwoWay 绑定到设备下拉框，
        /// 赋值即触发台账加载与扫描（自动选中与手动选择走同一入口）。</summary>
        public UsbStorageDevice? SelectedDevice
        {
            get => field;
            set
            {
                if (ReferenceEquals(field, value)) return;
                SetProperty(ref field, value);
                _selectionTasks.RemoveAll(static task => task.IsCompleted);
                _selectionTasks.Add(SelectCoreAsync(value));
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

        /// <summary>选择核心：清空旧台账 → 载入该设备历史台账 → 后台重扫设备文件并刷新。
        /// device 为 null 时仅清空（设备全部移除/用户取消选择）。</summary>
        private async Task SelectCoreAsync(UsbStorageDevice? device)
        {
            _scanCts?.Cancel();
            using var scan = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
            _scanCts = scan;
            var ct = scan.Token;
            MusicOnDevice.Clear();
            RaiseDeviceMusicChanged();
            try
            {
                if (device is null || _stopped) return;
                await LoadDeviceMusicAsync(device.UniqueId, ct);
                ct.ThrowIfCancellationRequested();
                await Task.Run(() => device.IsPortable
                    ? _musicDatabaseService.ScanPortableUsbDeviceAsync(device, ct)
                    : _musicDatabaseService.ScanUsbDeviceAsync(device.Path, device.UniqueId), ct);
                await LoadDeviceMusicAsync(device.UniqueId, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描 USB 设备失败: {Device}", device?.Name);
            }
            finally
            {
                if (ReferenceEquals(_scanCts, scan)) _scanCts = null;
            }
        }

        /// <summary>清空选中与台账（设备全部移除时）。选中置 null 会经由属性触发台账清理。</summary>
        public void ClearSelection()
        {
            _scanCts?.Cancel();
            SelectedDevice = null;
        }

        /// <summary>用户发送优先于后台扫描，不清除已加载的台账。</summary>
        public void CancelScanForExport() => _scanCts?.Cancel();

        /// <summary>每首音频落盘后发布状态；同路径覆盖，同标题的不同文件均保留。</summary>
        public void AddSentRecords(IReadOnlyList<UsbDeviceMusic> sent, UsbStorageDevice device)
        {
            if (sent.Count == 0 || _stopped || SelectedDevice?.UniqueId != device.UniqueId) return;
            _scanCts?.Cancel(); // 旧查询不得在发送完成后再覆盖当前台账。
            var changedTitles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in sent)
            {
                int index = -1;
                for (int i = 0; i < MusicOnDevice.Count; i++)
                {
                    if (!string.Equals(MusicOnDevice[i].Path, record.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    index = i;
                    break;
                }
                if (index >= 0)
                {
                    changedTitles.Add(MusicOnDevice[index].Title);
                    MusicOnDevice[index] = record;
                }
                else
                    MusicOnDevice.Add(record);
                changedTitles.Add(record.Title);
            }
            RaiseDeviceMusicChanged(changedTitles);
        }

        private async Task LoadDeviceMusicAsync(string uniqueId, CancellationToken token)
        {
            var records = await _musicDatabaseService.GetUsbDeviceMusics(uniqueId) ?? [];
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (_stopped || token.IsCancellationRequested || SelectedDevice?.UniqueId != uniqueId) return;
                MusicOnDevice.Clear();
                foreach (var r in records) MusicOnDevice.Add(r);
                RaiseDeviceMusicChanged();
            });
        }

        private void RaiseDeviceMusicChanged(IReadOnlyCollection<string>? changedTitles = null)
            => DeviceMusicChanged?.Invoke(this, new UsbDeviceMusicChangedEventArgs(changedTitles));
    }

    public sealed class UsbDeviceMusicChangedEventArgs(IReadOnlyCollection<string>? changedTitles) : EventArgs
    {
        public IReadOnlyCollection<string>? ChangedTitles { get; } = changedTitles;
    }
}
