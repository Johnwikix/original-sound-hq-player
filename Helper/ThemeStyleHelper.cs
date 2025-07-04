using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using Windows.UI;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    public class ThemeStyleHelper
    {
        public event EventHandler ThemeChanged;
        public event EventHandler StyleChanged;

        private Window _window;
        private AppWindow _appWindow;

        public ThemeStyleHelper(Window window, AppWindow appWindow)
        {
            _window = window;
            _appWindow = appWindow;

        }
        public void SetAppStyle()
        {
            try
            {
                var uiColor = Color.FromArgb(255, 32, 32, 32);
                _window.SystemBackdrop = null;
                switch (AppSettings.AppStyle)
                {
                    case "Acrylic":
                        if (AppSettings.AppTheme == "Default")
                        {
                            if (Application.Current.RequestedTheme != ApplicationTheme.Dark)
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        else
                        {
                            if (AppSettings.AppTheme == "Light")
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        var acrylic = new CustomAcrylicSystemBackdrop
                        {
                            TintOpacity = 0.5f,
                            LuminosityOpacity = 0.8f,
                            TintColor = uiColor
                        };
                        _window.SystemBackdrop = acrylic;
                        break;
                    case "TransparentAcrylic":
                        float colorOpacity = 0.4f;
                        if (AppSettings.AppTheme == "Default")
                        {
                            if (Application.Current.RequestedTheme != ApplicationTheme.Dark)
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        else
                        {
                            if (AppSettings.AppTheme == "Light")
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        var customAcrylic = new CustomAcrylicSystemBackdrop
                        {
                            TintOpacity = 0,
                            LuminosityOpacity = colorOpacity,
                            TintColor = uiColor
                        };
                        _window.SystemBackdrop = customAcrylic;
                        break;
                    case "Mica":
                        if (AppSettings.AppTheme == "Default")
                        {
                            if (Application.Current.RequestedTheme != ApplicationTheme.Dark)
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        else
                        {
                            if (AppSettings.AppTheme == "Light")
                            {
                                uiColor = Color.FromArgb(255, 255, 255, 255);
                            }
                        }
                        var mica = new CustomMicaSystemBackdrop
                        {
                            MicaKind = MicaKind.Base,
                            TintOpacity = 0.8f,
                            TintColor = uiColor
                        };
                        _window.SystemBackdrop = mica;
                        break;
                    default:
                        _window.SystemBackdrop = new DesktopAcrylicBackdrop();
                        break;
                }
                StyleChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAppStyle error: {ex.Message}");
            }
        }

        public void SetAppTheme()
        {
            try
            {
                AppWindowTitleBar titleBar = _appWindow.TitleBar;
                if (_window.Content is FrameworkElement rootElement)
                {
                    switch (AppSettings.AppTheme)
                    {
                        case "Default":
                            titleBar.ButtonForegroundColor = null;
                            titleBar.ButtonHoverForegroundColor = null;
                            titleBar.ButtonPressedForegroundColor = null;
                            titleBar.ButtonHoverBackgroundColor = null;
                            titleBar.ButtonPressedBackgroundColor = null;
                            rootElement.RequestedTheme = ElementTheme.Default;
                            AppSettings.elementTheme = ElementTheme.Default;
                            break;
                        case "Dark":
                            rootElement.RequestedTheme = ElementTheme.Dark;
                            AppSettings.elementTheme = ElementTheme.Dark;
                            titleBar.ButtonForegroundColor = Colors.White;
                            titleBar.ButtonHoverForegroundColor = Colors.White;
                            titleBar.ButtonPressedForegroundColor = Colors.White;
                            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
                            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 80, 80, 80);
                            break;
                        case "Light":
                            rootElement.RequestedTheme = ElementTheme.Light;
                            AppSettings.elementTheme = ElementTheme.Light;
                            titleBar.ButtonForegroundColor = Colors.Black;
                            titleBar.ButtonHoverForegroundColor = Colors.Black;
                            titleBar.ButtonPressedForegroundColor = Colors.Black;
                            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 220, 220, 220);
                            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 190, 190, 190);
                            break;
                        default:
                            titleBar.ButtonForegroundColor = null;
                            titleBar.ButtonHoverForegroundColor = null;
                            titleBar.ButtonPressedForegroundColor = null;
                            titleBar.ButtonHoverBackgroundColor = null;
                            titleBar.ButtonPressedBackgroundColor = null;
                            rootElement.RequestedTheme = ElementTheme.Default;
                            AppSettings.elementTheme = ElementTheme.Default;
                            break;
                    }
                    ThemeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAppTheme error: {ex.Message}");
            }
        }
    }
}
