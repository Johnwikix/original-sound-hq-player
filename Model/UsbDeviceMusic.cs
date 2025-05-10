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

    }
}
