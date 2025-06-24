using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    public sealed partial class ProgressDialog : ContentDialog
    {
        public ProgressDialog(string statusMessage)
        {
            this.InitializeComponent();
            statusText.Text = statusMessage;
        }

        public async Task UpdateProgress(int value)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                progressBar.Value = value;
                progressText.Text = $"{value}%";
                if (value == 100)
                {
                    this.Hide();
                }
            });
        }

        public void SetStatusMessage(string title)
        {
            statusText.Text = title;
        }

        private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // 这里可以添加取消操作的逻辑
        }
    }
}
