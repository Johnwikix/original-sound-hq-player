using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

namespace WinUIMusicPlayer.View
{
    internal interface ILibraryTab
    {
        void Activate();
        void Deactivate();
    }

    /// <summary>曲库页签宿主。页签视图按需创建，并在当前曲库实例内复用。</summary>
    public sealed partial class MusicBrowsePage : Page
    {
        private bool _active;
        private bool _navigatedAway;

        public MusicBrowseViewModel ViewModel { get; }

        internal Page? ActiveTabPage => _active ? TabPresenter.Content as Page : null;

        // 空库占位覆盖整个内容区；收藏在仍有曲目时继续显示。
        public static Visibility GetEmptyVisibility(Visibility libraryEmpty, string tab, int favoriteCount) =>
            tab == "favourite" && favoriteCount > 0 ? Visibility.Collapsed : libraryEmpty;

        public static Visibility InvertVisibility(Visibility value) =>
            value == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        public MusicBrowsePage()
        {
            ViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            InitializeComponent();
            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _navigatedAway = false;
            ActivateView();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _navigatedAway = true;
            DeactivateView();
            base.OnNavigatedFrom(e);
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (!_navigatedAway) ActivateView();
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) DeactivateView();
        }

        private void ActivateView()
        {
            if (_active) return;
            ViewModel.InitializeSelectedPage();
            _active = true;
            ViewModel.PropertyChanged += OnBrowsePropertyChanged;
            ShowSelectedTab();
            Bindings.Initialize();
        }

        private void DeactivateView()
        {
            if (!_active) return;
            if (ActiveTabPage is ILibraryTab tab) tab.Deactivate();
            _active = false;
            ViewModel.PropertyChanged -= OnBrowsePropertyChanged;
            Bindings.StopTracking();
        }

        private void OnBrowsePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.SelectedPageTag)) ShowSelectedTab();
        }

        private void ShowSelectedTab()
        {
            var tag = ViewModel.SelectedPageTag;
            var selected = tag switch
            {
                "album" => AlbumTab,
                "artist" => ArtistTab,
                "favourite" => FavouriteTab,
                _ => SongTab
            };
            // SwitchPresenter snapshots Case.Content when Value changes. Populate each case
            // on its first visit so it attaches only the selected page and reuses that page later.
            var page = (Page)(selected.Content ??= tag switch
            {
                "album" => new AlbumPage(),
                "artist" => new ArtistPage(),
                "favourite" => new FavouritePlayListPage(),
                _ => new SongListPage()
            });

            if (ActiveTabPage is ILibraryTab previous && !ReferenceEquals(previous, page))
                previous.Deactivate();

            AppData.CurrentPage = page.GetType();
            if (tag is "song" or "favourite")
                ViewModel.AppViewModel.PageType = tag;
            ((ILibraryTab)page).Activate();
            TabPresenter.Value = tag;
            ViewModel.AppViewModel.RefreshDataSource();
        }

        private void SelectPage_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string tag)
                SelectBarItem(tag);
        }

        public SelectorBarItem? SelectorItem(string tag)
        {
            foreach (var item in selectPage.Items)
                if (Equals(item.Tag, tag)) return item;
            return null;
        }

        public void SelectBarItem(string name)
        {
            if (MusicBrowseViewModel.IsLibraryTab(name))
                ViewModel.SelectedPageTag = name;
        }

        // USB 设备选择由 ComboBox SelectedItem 双向绑定 UsbDeviceService.SelectedDevice 完成，
        // 台账加载与扫描在服务内触发，此处无需代码后置逻辑。

        private void EmptyLibraryGrid_DragOver(object sender, DragEventArgs e)
        {
            bool canDrop = ViewModel.DropFoldersFromEmptyCommand.CanExecute(null)
                && e.DataView.Contains(StandardDataFormats.StorageItems);
            e.AcceptedOperation = canDrop
                ? DataPackageOperation.Link
                : DataPackageOperation.None;
            DropOverlay.Visibility = canDrop ? Visibility.Visible : Visibility.Collapsed;
        }

        private void EmptyLibraryGrid_DragLeave(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private void EmptyLibraryGrid_Unloaded(object sender, RoutedEventArgs e)
        {
            // 卸载时清理瞬时拖放反馈。
            DropOverlay.Visibility = Visibility.Collapsed;
        }

        private async void EmptyLibraryGrid_Drop(object sender, DragEventArgs e)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (!ViewModel.DropFoldersFromEmptyCommand.CanExecute(null)
                || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var folders = items.AsValueEnumerable()
                    .Where(item => item.IsOfType(Windows.Storage.StorageItemTypes.Folder))
                    .ToList();
                if (folders.Count > 0)
                {
                    await ViewModel.DropFoldersFromEmptyCommand.ExecuteAsync(folders);
                }
            }
            catch (Exception ex)
            {
                App.GetLogger<MusicBrowsePage>().LogError(ex, $"EmptyLibraryGrid_Drop 拖放文件夹失败: {ex.Message}");
            }
        }

        public void SelectBarAlbum(string Album)
        {
            ViewModel.AppViewModel.PageType = "album";
            ViewModel.AppViewModel.CurrentAlbumObj = ViewModel.AppViewModel.FindFirstByAlbum(Album);
            if (ActiveTabPage is AlbumPage albumPage)
            {
                albumPage.EnterDetailFromCrossLink();
            }
            else
            {
                SelectBarItem("album");
            }
        }

        public void SelectBarArtist(string artist)
        {
            ViewModel.AppViewModel.PageType = "artist";
            ViewModel.AppViewModel.CurrentArtistObj = ViewModel.AppViewModel.FindFirstByArtist(artist);
            if (ActiveTabPage is ArtistPage artistPage)
            {
                artistPage.EnterDetailFromCrossLink();
            }
            else
            {
                SelectBarItem("artist");
            }
        }

        public void BackButton()
        {
            if (ActiveTabPage is AlbumPage albumPage && albumPage.ViewModel.IsInDetailMode)
            {
                albumPage.CollapseDetail();
                return;
            }
            if (ActiveTabPage is ArtistPage artistPage && artistPage.ViewModel.IsInDetailMode)
            {
                artistPage.CollapseDetail();
                return;
            }

        }

    }
}
