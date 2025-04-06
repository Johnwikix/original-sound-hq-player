using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class SaveSettings
    {
        [PrimaryKey]
        public int Id { get; set; } = 1;
        public string OutputMode { get; set; } = "WasapiExclusive";
        public int Latency { get; set; } = 200;
        public string Name { get; set; }
        public string DeviceFriendlyName { get; set; }
        public string DefualtEntry { get; set; }
        public string DefualtPlayList { get; set; }
    }
}
