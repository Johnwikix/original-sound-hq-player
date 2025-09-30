namespace WinUIMusicPlayer.Model
{
    public class BassOutputDevice
    {
        public int Id { get; set; } = -1;
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string OutputMode { get; set; } = string.Empty;
        public int AsioId { get; set; } = -1;
    }
}
