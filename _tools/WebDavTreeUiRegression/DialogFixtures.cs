using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;

// 浏览对话框只验证模板；连接对话框链接真实代码和 ViewModel，使用本地 HTTP fixture。
namespace WinUIMusicPlayer.View.SubView
{
    public sealed class DialogFixture
    {
        public string Name { get; set; } = "Fixture";
        public string Address { get; set; } = "https://example.invalid/dav/";
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public string Status => "";
        public string CertificateDetails => "";
        public bool HasCertificate => false;
        public bool HasTrustedCertificate => false;
        public bool HasFolders => true;
        public bool ReadMetadata { get; set; }
        public bool ScanOnStartup { get; set; }
        public bool Enabled { get; set; } = true;
        public int SelectedTrackCount => 0;
        public ICommand? ConnectCommand => null;
        public ICommand? TrustCertificateCommand => null;
        public ICommand? ForgetCertificateCommand => null;
        public ICommand? PlaySelectedCommand => null;
    }

    public sealed partial class WebDavBrowserDialog : ContentDialog
    {
        public DialogFixture ViewModel { get; } = new();
        public TreeView TestTree => BrowserTree;
        public WebDavBrowserDialog() => InitializeComponent();
        private void Tree_Expanding(TreeView sender, TreeViewExpandingEventArgs args) { }
        private void Tree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) { }
        private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) { }
    }

    public sealed partial class WebDavConnectionDialog : ContentDialog
    {
        public TreeView TestTree => FolderTree;
    }
}

namespace WinUIMusicPlayer.Utils
{
    public static class BindUtils
    {
        public static Visibility BoolToVisibilityConverter(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    }
}
