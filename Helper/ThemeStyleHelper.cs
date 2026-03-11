using DevWinUI;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using Windows.UI;
using WinUIEx;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper
{
    public class ThemeStyleHelper
    {
        public event EventHandler ThemeChanged;
        public event EventHandler StyleChanged;
        public event EventHandler CustomStyleChanged;

        private Window _window;
        private AppWindow _appWindow;
        private CustomAcrylicSystemBackdrop _acrylicSystemBackdrop;
        private CustomMicaSystemBackdrop _micaSystemBackdrop;
        private TransparentTintBackdrop _transparentTintBackdrop;

        public ThemeStyleHelper(Window window, AppWindow appWindow)
        {
            _window = window;
            _appWindow = appWindow;
            _acrylicSystemBackdrop = new CustomAcrylicSystemBackdrop(window) { 
                IsInputActive = AppSettings.IsUpdateBackDrop
            };
            _micaSystemBackdrop = new CustomMicaSystemBackdrop(window) {
                IsInputActive = AppSettings.IsUpdateBackDrop
            };
            _transparentTintBackdrop = new TransparentTintBackdrop(Colors.Transparent);

        }

        private static Color GetUiColor() {
            var isDarkTheme = AppSettings.AppTheme switch
            {
                "Dark" => true,
                "Light" => false,
                "Default" => Application.Current.RequestedTheme == ApplicationTheme.Dark,
                _ => true
            };
            return isDarkTheme
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 255, 255, 255);
        }

        public void SetAppStyle()
        {
            try
            {
                
                var backdrop = _window.SystemBackdrop as CustomAcrylicSystemBackdrop;
                switch (AppSettings.AppStyle)
                {
                    case "Acrylic":
                        if (backdrop is not null)
                        {
                            backdrop.UpdateProperties(0.5f, 0.8f, GetUiColor());
                        }
                        else
                        {
                            _acrylicSystemBackdrop.TintOpacity = 0.5f;
                            _acrylicSystemBackdrop.LuminosityOpacity = 0.8f;
                            _acrylicSystemBackdrop.TintColor = GetUiColor();
                            _window.SystemBackdrop = _acrylicSystemBackdrop;
                        }
                        break;
                    case "TransparentAcrylic":
                        if (backdrop is not null)
                        {
                            backdrop.UpdateProperties(0, 0.4f, GetUiColor());
                        }
                        else
                        {
                            _acrylicSystemBackdrop.TintOpacity = 0;
                            _acrylicSystemBackdrop.LuminosityOpacity = 0.4f;
                            _acrylicSystemBackdrop.TintColor = GetUiColor();
                            _window.SystemBackdrop = _acrylicSystemBackdrop;
                        }                        
                        break;
                    case "Mica":
                        if (_window.SystemBackdrop is not CustomMicaSystemBackdrop)
                        {
                            _window.SystemBackdrop = _micaSystemBackdrop;
                        }
                        break;
                    case "TransparentTint":
                        if (_window.SystemBackdrop is not TransparentTintBackdrop)
                        {
                            _window.SystemBackdrop = _transparentTintBackdrop;
                        }
                        break;
                    case "CustomAcrylicStyle":
                        if (backdrop is not null)
                        {
                            backdrop.UpdateProperties(1.0,
                                            AppSettings.CustomAcrylicOpacity,
                                            Color.FromArgb(AppSettings.CustomColorAlpha,
                                                AppSettings.CustomColorRed,
                                                AppSettings.CustomColorGreen,
                                                AppSettings.CustomColorBlue));
                        }
                        else
                        {
                            _acrylicSystemBackdrop.TintOpacity = 1.0;
                            _acrylicSystemBackdrop.LuminosityOpacity = AppSettings.CustomAcrylicOpacity;
                            _acrylicSystemBackdrop.TintColor = Color.FromArgb(AppSettings.CustomColorAlpha,
                                                        AppSettings.CustomColorRed,
                                                        AppSettings.CustomColorGreen,
                                                        AppSettings.CustomColorBlue);
                            _window.SystemBackdrop = _acrylicSystemBackdrop;
                        }                        
                        break;
                    default:
                        _acrylicSystemBackdrop.TintOpacity = 0.5f;
                        _acrylicSystemBackdrop.LuminosityOpacity = 0.8f;
                        _acrylicSystemBackdrop.TintColor = GetUiColor();
                        _window.SystemBackdrop = _acrylicSystemBackdrop;
                        break;
                }
                StyleChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetAppStyle error: {ex.Message}");
            }
        }

        public void ChangeCustomAcrylicStyle()
        {
            if (AppSettings.AppStyle == "CustomAcrylicStyle")
            {
                if (_window.SystemBackdrop is CustomAcrylicSystemBackdrop backdrop)
                {
                    backdrop.UpdateProperties(1.0,
                        AppSettings.CustomAcrylicOpacity,
                        Color.FromArgb(AppSettings.CustomColorAlpha,
                            AppSettings.CustomColorRed,
                            AppSettings.CustomColorGreen,
                            AppSettings.CustomColorBlue));
                    CustomStyleChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void UpdateBackdropActiveState(bool IsActive)
        {
            _acrylicSystemBackdrop.IsInputActive = IsActive;
            _micaSystemBackdrop.IsInputActive = IsActive;
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
                            AppSettings.ElementTheme = ElementTheme.Default;
                            break;
                        case "Dark":
                            rootElement.RequestedTheme = ElementTheme.Dark;
                            AppSettings.ElementTheme = ElementTheme.Dark;
                            titleBar.ButtonForegroundColor = Colors.White;
                            titleBar.ButtonHoverForegroundColor = Colors.White;
                            titleBar.ButtonPressedForegroundColor = Colors.White;
                            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(255, 50, 50, 50);
                            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(255, 80, 80, 80);
                            break;
                        case "Light":
                            rootElement.RequestedTheme = ElementTheme.Light;
                            AppSettings.ElementTheme = ElementTheme.Light;
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
                            AppSettings.ElementTheme = ElementTheme.Default;
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
