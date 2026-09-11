namespace WinUIMusicPlayer.Model
{
    public class BassOutputDevice
    {
        /// <summary>获取或设置稳定的 WASAPI 端点 ID。</summary>
        public string? EndpointId { get; set; }
        public int Id { get; set; } = -1;
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string OutputMode { get; set; } = string.Empty;
        public int AsioId { get; set; } = -1;
    }
}
