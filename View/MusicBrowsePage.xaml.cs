using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SQLite;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MusicBrowsePage : Page
    {
        private SQLiteAsyncConnection dbConnection;
        private MediaPlayer mediaPlayer;
        private Music currentPlayingMusic;
        private bool isPlaying;

        public MusicBrowsePage()
        {
            this.InitializeComponent();
            InitializeDatabase();
            mediaPlayer = new MediaPlayer();
        }

        private async void InitializeDatabase()
        {
            var dbPath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicDatabase.db");
            dbConnection = new SQLiteAsyncConnection(dbPath);
            await dbConnection.CreateTableAsync<Music>();
            await LoadMusicAsync();
        }

        private async Task LoadMusicAsync()
        {
            try
            {
                var musicList = await dbConnection.Table<Music>().ToListAsync();
                MusicListView.ItemsSource = musicList;
            }
            catch (SQLiteException ex)
            {
                System.Diagnostics.Debug.WriteLine($"SQLite ¥ÌŒÛ: {ex.Message}");
            }
        }

        private async void MusicListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            var selectedMusic = MusicListView.SelectedItem as Music;
            if (selectedMusic != null)
            {
                await PlayMusic(selectedMusic);
            }
        }

        private async void PlayButton_Click(object sender, RoutedEventArgs e)
        {
            if (isPlaying)
            {
                mediaPlayer.Pause();
                isPlaying = false;
            }
            else {
                mediaPlayer.Play();
                isPlaying = true;
            }
            UpdatePlayPauseButtonIcon();
        }

        private void UpdatePlayPauseButtonIcon()
        {
            if (isPlaying)
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769"; // ‘›Õ£Õº±Í
            }
            else
            {
                ((FontIcon)PlayPauseButton.Content).Glyph = "\uE768"; // ≤•∑≈Õº±Í
            }
        }

        private async Task PlayMusic(Music music)
        {
            if (mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                mediaPlayer.Pause();
            }

            currentPlayingMusic = music;
            MusicTitleTextBlock.Text = music.Title;
            MusicAuthorTextBlock.Text = music.Author;

            if (music.Cover != null)
            {
                using (var ms = new MemoryStream(music.Cover))
                {
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                    AlbumCoverImage.Source = bitmapImage;
                }
            }

            var file = await StorageFile.GetFileFromPathAsync(music.Path);
            var stream = await file.OpenAsync(FileAccessMode.Read);
            mediaPlayer.Source = MediaSource.CreateFromStream(stream, file.ContentType);
            mediaPlayer.Play();
            ((FontIcon)PlayPauseButton.Content).Glyph = "\uE769";
            isPlaying = true;
        }

        private async void RemoveMusicButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null && button.Tag is int musicId)
            {
                await dbConnection.DeleteAsync<Music>(musicId);
                await LoadMusicAsync();
            }
        }
    }
}
