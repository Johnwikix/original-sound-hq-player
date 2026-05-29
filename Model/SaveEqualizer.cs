using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class SaveEqualizer
    {
        [PrimaryKey]
        public int Id { get; set; } = 1; // 固定 ID 为 1，方便管理
        public string EqualizerStr { get; set; } = string.Empty;
        public bool IsEqualizerEnabled { get; set; } = false;
        public string EqualizerPreset { get; set; } = "Flat";
    }
}
