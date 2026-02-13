using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class PlayListPage : Page, INavigatable
    {
        public PlayListViewModel ViewModel { get; }
        public PlayListPage(PlayListViewModel viewModel)
        {
            this.InitializeComponent();
            ViewModel = viewModel;
            ViewModel.SetCurrentPage(this);
            DataContext = this;
        }

        public void ReceiveNavigationParameter(object parameter)
        {
            ViewModel.ReceiveNavigation();
        }

        private void RemovePlayListButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                ViewModel.RemovePlayList(playList);
            }
        }

        private void EditPlayListNameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                ViewModel.AppViewModel.EditPlayListName(playList, async () =>
                {
                    ContentDialog contentDialog = new ContentDialog
                    {
                        Title = ToolUtils.GetString("ModifyPlaylist"),
                        Content = new TextBox { Text = $"{playList.Name}" },
                        PrimaryButtonText = ToolUtils.GetString("PrimaryButton"),
                        CloseButtonText = ToolUtils.GetString("CloseButton"),
                        XamlRoot = this.XamlRoot
                    };
                    contentDialog.RequestedTheme = AppSettings.elementTheme;
                    ContentDialogResult result = await contentDialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        TextBox textBox = (TextBox)contentDialog.Content;
                        return textBox.Text;
                    }

                    return string.Empty;
                });
            }
        }
        //private void PlayListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        //{
        //    var playList = PlayListView.SelectedItem as PlayList;
        //    ViewModel.PlayListView_SelectionChanged(playList);
        //}

        private void ExportPlayList_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is PlayList playList)
            {
                ViewModel.ExportPlayList(playList);
            }
        }
    }
}
