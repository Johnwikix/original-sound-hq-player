using SQLite;

namespace WinUIMusicPlayer.Model
{
    /// <summary>用户自定义均衡器预设：任意数量，可自定义名称。</summary>
    public class SaveEqualizerPreset
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        /// <summary>EqPreset JSON（频段增益 + 预留 Q 值）。</summary>
        public string EqualizerStr { get; set; } = string.Empty;
    }
}
