using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.ViewModel;
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
        private CancellationTokenSource? _scanCts;

        /// <summary>当前枚举到的 USB 存储设备（下拉框数据源）。</summary>
        public ObservableCollection<UsbStorageDevice> Devices { get; } = [];

        /// <summary>当前选中的设备；null 表示未选择。</summary>
        public UsbStorageDevice? SelectedDevice { get; private set => SetProperty(ref field, value); }

        /// <summary>选中设备的音乐发送台账（含历史扫描与本次会话的发送记录）。</summary>
        public ObservableCollection<UsbDeviceMusic> MusicOnDevice { get; } = [];

        private Visibility _devicesVisibility = Visibility.Collapsed;
        /// <summary>设备下拉框的可见性（无设备时隐藏）。</summary>
        public Visibility DevicesVisibility { get => _devicesVisibility; private set => SetProperty(ref _devicesVisibility, value); }

        /// <summary>设备列表变化（插入/移除/枚举完成）→ 右键菜单同步。</summary>
        public event EventHandler? DevicesChanged;
        /// <summary>发送台账变化 → 依设备标记（IsExistOnDevice）刷新。</summary>
        public event EventHandler? DeviceMusicChanged;

        public UsbDeviceService(MusicDatabaseService musicDatabaseService, ILogger<UsbDeviceService> logger)
        {
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
            StartWatching();
        }

        private void StartWatching()
        {
            try
            {
                string deviceSelector = StorageDevice.GetDeviceSelector();
                _deviceWatcher = DeviceInformation.CreateWatcher(deviceSelector);
                _deviceWatcher.Added += async (_, _) =>
                {
                    await Task.Delay(1500); // 等待设备挂载稳定
                    await RefreshDevicesAsync();
                };
                _deviceWatcher.Removed += async (_, _) => await RefreshDevicesAsync();
                _deviceWatcher.Start();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"启动 USB 设备监视器失败: {ex.Message}");
            }
        }

        /// <summary>重新枚举 USB 存储设备并更新设备列表/可见性。</summary>
        public async Task RefreshDevicesAsync()
        {
            try
            {
                var devices = await UsbStorageDeviceReader.GetUsbStorageDevicesAsync();
                var dq = App.MainWindow.DispatcherQueue;
                await dq.EnqueueAsync(() =>
                {
                    Devices.Clear();
                    foreach (var d in devices) Devices.Add(d);
                    DevicesVisibility = Devices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    if (Devices.Count == 0) ClearSelection();
                });
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"枚举 USB 设备失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 选择设备：清空旧台账 → 载入该设备的历史台账 → 后台重新扫描设备上的文件并刷新。
        /// device 为 null 时仅清空选择。
        /// </summary>
        public async Task SelectAsync(UsbStorageDevice? device)
        {
            SelectedDevice = device;
            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            MusicOnDevice.Clear();
            RaiseDeviceMusicChanged();
            if (device is null) return;

            await LoadDeviceMusicAsync(device.UniqueId);
            try
            {
                await Task.Run(() => _musicDatabaseService.ScanUsbDeviceAsync(device.Path, device.UniqueId), ct);
                if (!ct.IsCancellationRequested)
                    await LoadDeviceMusicAsync(device.UniqueId);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"扫描 USB 设备失败: {device.Path}: {ex.Message}");
            }
        }

        /// <summary>清空选中设备与台账（设备全部移除时）。</summary>
        public void ClearSelection()
        {
            _scanCts?.Cancel();
            SelectedDevice = null;
            if (MusicOnDevice.Count > 0)
            {
                MusicOnDevice.Clear();
                RaiseDeviceMusicChanged();
            }
        }

        /// <summary>记录一次发送（已存在同标题记录时幂等跳过），并触发状态标记刷新。</summary>
        public void AddSentRecords(IEnumerable<(Music Music, string Extension)> sent, UsbStorageDevice device)
        {
            foreach (var (music, extension) in sent)
            {
                if (MusicOnDevice.AsValueEnumerable().Any(m => m.Title == music.Title))
                    continue;
                MusicOnDevice.Add(new UsbDeviceMusic
                {
                    Title = music.Title,
                    Author = music.Author,
                    Album = music.Album,
                    Extension = extension,
                    UniqueDeviceId = device.UniqueId,
                });
            }
            RaiseDeviceMusicChanged();
        }

        private async Task LoadDeviceMusicAsync(string uniqueId)
        {
            var records = await _musicDatabaseService.GetUsbDeviceMusics(uniqueId) ?? [];
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                MusicOnDevice.Clear();
                foreach (var r in records) MusicOnDevice.Add(r);
            });
            RaiseDeviceMusicChanged();
        }

        private void RaiseDeviceMusicChanged() => DeviceMusicChanged?.Invoke(this, EventArgs.Empty);
    }
}
