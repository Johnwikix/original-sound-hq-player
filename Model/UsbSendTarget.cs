namespace WinUIMusicPlayer.Model
{
    /// <summary>
    /// "发送到 USB 设备"菜单叶子的参数：设备 + 可选的发送前转换格式。
    /// Format 为 null 表示原格式直传；否则按 Format/BitrateKbps 转换后写入设备。
    /// </summary>
    public class UsbSendTarget
    {
        public UsbStorageDevice Device { get; init; } = null!;
        public string? Format { get; init; }
        public int BitrateKbps { get; init; } = 320;
    }
}
