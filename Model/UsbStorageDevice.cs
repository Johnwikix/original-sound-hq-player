namespace WinUIMusicPlayer.Model
{
    public class UsbStorageDevice : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public string Path
        {
            get;
            set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(DisplayName)); }
        } = string.Empty;
        public string Name
        {
            get;
            set { if (SetProperty(ref field, value)) OnPropertyChanged(nameof(DisplayName)); }
        } = string.Empty;
        public double FreeSpaceInGB { get; set => SetProperty(ref field, value); }
        public double TotalSpaceInGB { get; set => SetProperty(ref field, value); }
        public string UniqueId { get; set; } = string.Empty;
        public string? PortableDeviceId
        {
            get;
            set
            {
                if (!SetProperty(ref field, value)) return;
                OnPropertyChanged(nameof(IsPortable));
                OnPropertyChanged(nameof(DisplayName));
            }
        }
        public string? PortableStorageId { get; set; }
        public bool IsPortable => PortableDeviceId is not null;
        public string DisplayName => IsPortable ? Name : Path;

        /// <summary>后台扫描/导出使用地址快照，设备重枚举不能在途中改写其目标地址。</summary>
        public UsbStorageDevice CreateSnapshot() => new()
        {
            Path = Path,
            Name = Name,
            FreeSpaceInGB = FreeSpaceInGB,
            TotalSpaceInGB = TotalSpaceInGB,
            UniqueId = UniqueId,
            PortableDeviceId = PortableDeviceId,
            PortableStorageId = PortableStorageId
        };
    }
}
