using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class UsbDeviceMusic
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Extension { get; set; }
        public string Album { get; set; }
        public string UniqueDeviceId { get; set; }

        // null 表示旧台账尚未建立指纹；标签成功读取后才允许跳过后续补读。
        public long? ScanFileSize { get; set; }
        public long? ScanModifiedTicks { get; set; }
        public bool? ScanMetadataRead { get; set; }

    }
}
