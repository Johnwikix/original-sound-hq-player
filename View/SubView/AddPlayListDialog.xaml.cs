using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView
{
    public sealed partial class AddPlayListDialog : ContentDialog
    {
        private readonly AppViewModel _appViewModel;
        private bool _confirmed;

        public AddPlayListDialog(AppViewModel appViewModel)
        {
            InitializeComponent();
            _appViewModel = appViewModel;
            Title = ToolUtils.GetString("FlyoutAddToPlaylist");
            NameTextBox.PlaceholderText = ToolUtils.GetString("EnterPlaylistName");
            ImportButtonText.Text = ToolUtils.GetString("ImportM3u8");
            ConfirmButton.Content = ToolUtils.GetString("PrimaryButton");
            CancelButton.Content = ToolUtils.GetString("CloseButton");
        }

        public async Task<string?> ShowAndGetNameAsync(XamlRoot xamlRoot)
        {
            NameTextBox.Text = string.Empty;
            _confirmed = false;
            await this.ShowThemedAsync(xamlRoot);
            if (_confirmed && !string.IsNullOrEmpty(NameTextBox.Text))
                return NameTextBox.Text;
            return null;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            _confirmed = true;
            Hide();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private async void ImportM3u8Button_Click(object sender, RoutedEventArgs e)
        {
            List<PlayList> newPlaylists = await ToolUtils.OpenM3u8File();
            if (newPlaylists is not null && newPlaylists.Count > 0)
            {
                for (int i = newPlaylists.Count - 1; i >= 0; i--)
                {
                    int id = newPlaylists[i].Id;
                    for (int j = 0; j < _appViewModel.AllPlayList.Count; j++)
                    {
                        if (_appViewModel.AllPlayList[j].Id == id)
                        {
                            newPlaylists.RemoveAt(i);
                            break;
                        }
                    }
                }
                if (newPlaylists.Count > 0)
                    await _appViewModel.AllPlayList.AddRangeAsync(newPlaylists);
            }
            Hide();
        }
    }
}
