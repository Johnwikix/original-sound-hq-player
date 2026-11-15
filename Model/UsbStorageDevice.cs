namespace WinUIMusicPlayer.Model
{
    public class UsbStorageDevice
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double FreeSpaceInGB { get; set; }
        public double TotalSpaceInGB { get; set; }
        public string UniqueId { get; set; } = string.Empty;
        public string? PortableDeviceId { get; set; }
        public string? PortableStorageId { get; set; }
        public bool IsPortable => PortableDeviceId is not null;
        public string DisplayName => IsPortable ? Name : Path;
    }
}
