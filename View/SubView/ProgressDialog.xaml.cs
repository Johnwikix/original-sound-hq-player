using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;

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

        private void ContentDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            // 这里可以添加取消操作的逻辑
        }
    }
}
