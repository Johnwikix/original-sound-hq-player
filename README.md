# 🎵 Original Sound HQ Player 🎶

A modern and feature-rich music player application built with WinUI, designed to provide a seamless and enjoyable music listening experience on Windows. This application allows users to browse their music library, manage playlists, discover new music, and enjoy high-quality audio playback. It leverages modern .NET technologies, including dependency injection, logging, and MVVM architecture, to create a maintainable and scalable codebase.

## 🚀 Key Features

- **🎵 Music Library Browsing:** Easily browse your local music library by song, artist, album, or genre.
- **❤️ Favorite Playlists:** Create and manage custom playlists of your favorite songs.
- **🎚️ Playback Controls:** Enjoy full playback controls, including play, pause, skip, shuffle, and repeat.
- **💾 Audio Conversion:** Convert audio files to different formats.
- **✨ Modern UI:** Experience a sleek and intuitive user interface built with WinUI.
- **⚙️ Settings:** Customize the application's behavior and appearance through settings.
- **USB Device Support:** Scan and import music from connected USB devices.
- **Single Instance Application:** Ensures only one instance of the application runs at a time.
- **Minimum Window Size Enforcement:** Prevents the window from being resized too small.
- **Database Support:** Uses SQLite for storing music library and playlist data.

## 🛠️ Tech Stack

*   **Frontend:**
    *   WinUI (Windows UI Library)
    *   XAML
    *   CommunityToolkit.WinUI.UI.Controls
    *   Microsoft.Xaml.Behaviors.WinUI.Managed
*   **Backend:**
    *   .NET 9
    *   C#
    *   CommunityToolkit.Mvvm
    *   Microsoft.Extensions.Hosting
    *   Microsoft.Extensions.DependencyInjection
    *   Microsoft.Extensions.Logging
    *   Serilog
    *   ManagedBass
    *   TagLibSharp
*   **Database:**
    *   SQLite
    *   sqlite-net-pcl
    *   SQLitePCLRaw.bundle_green
*   **Other:**
    *   Microsoft.WindowsAppSDK
    *   ZLinq

## 📦 Getting Started

Follow these instructions to get the project up and running on your local machine.

### Prerequisites

*   [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)
*   Windows 10 version 1809 or later
*   Visual Studio 2022 or later (with the WinUI workload installed)

### Installation

1. Clone the repository:

   ```bash
   git clone https://github.com/Johnwikix/original-sound-hq-player.git
   ```

3.  Restore NuGet packages:

    *   Right-click on the solution in Solution Explorer.
    *   Select "Restore NuGet Packages".

### Running Locally

1.  Build the solution:

    *   Press `Ctrl+Shift+B` or select "Build Solution" from the "Build" menu.

2.  Run the application:

    *   Press `Ctrl+F5` or select "Start Without Debugging" from the "Debug" menu.

## 📂 Project Structure

```
WinUIMusicPlayer/
├── App.xaml.cs               # Application entry point and initialization
├── MainWindow.xaml.cs          # Main window logic
├── WinUIMusicPlayer.csproj    # Project file
├── Package.appxmanifest       # Application manifest
├── Model/                    # Data models
│   ├── Music.cs
│   ├── PlayList.cs
│   └── UsbDeviceMusic.cs
├── ViewModel/                # ViewModels
│   ├── MusicBrowseViewModel.cs
│   ├── SongListViewModel.cs
│   ├── FavouritePlayListViewModel.cs
│   └── PlayListViewModel.cs
├── View/                     # Views (XAML pages)
│   ├── MusicBrowsePage.xaml.cs
│   ├── SongListPage.xaml.cs
│   ├── FavouritePlayListPage.xaml.cs
│   └── PlayListPage.xaml.cs
│   └── SubView/
│       └── ProgressDialog.xaml.cs
├── Helper/                   # Helper classes
│   ├── WindowSizeHelper.cs
│   ├── SingleInstanceHelper.cs
│   └── WindowHelper.cs       # (Potentially missing, inferred from SingleInstanceHelper)
├── Services/                 # Services (e.g., navigation, audio playback)
│   ├── NavigationService.cs
│   ├── AudioPlayerService.cs
│   ├── MusicDatabaseService.cs
│   ├── FilePickerService.cs
│   └── AudioConverterService.cs
├── Utils/                    # Utility functions
│   ├── ToolUtils.cs
│   └── AppSettings.cs
├── Reader/                   # Music metadata reader
│   └── MusicReader.cs
├── Assets/                   # Application assets (images, logos, etc.)
├── app.manifest              # Application manifest file
└── ...
```

## 📸 Screenshots

(Add screenshots of the application here to showcase its UI and features)

## 🤝 Contributing

Contributions are welcome! Please follow these steps:

1.  Fork the repository.
2.  Create a new branch for your feature or bug fix.
3.  Make your changes and commit them with descriptive messages.
4.  Push your changes to your fork.
5.  Submit a pull request.

## 📝 License

This project is licensed under the [MIT License](LICENSE).

## 📬 Contact

If you have any questions or suggestions, feel free to contact me at [your-email@example.com](mailto:your-email@example.com).

## 💖 Thanks

Thank you for checking out the WinUI Music Player! I hope you find it useful and enjoyable.

This README is written by [readme.ai](https://readme-generator-phi.vercel.app/).