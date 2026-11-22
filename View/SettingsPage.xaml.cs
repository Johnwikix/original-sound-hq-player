using CommunityToolkit.WinUI.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    /// <summary>设置页分区按需创建，重入时恢复界面初始位置。</summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly ConditionalWeakTable<FrameworkElement, SectionViewState> _sections = new();
        private int _visit;

        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            InitializeComponent();
            WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
            NavigationCacheMode = NavigationCacheMode.Enabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _visit++;
            SectionSelector.SelectedItem = GeneralSection;
            ViewModel.AppViewModel.IsRealDevceChange = false;
            _ = ViewModel.AppViewModel.GetWasapiDeviceAsync();
        }

        private void OnSectionLoaded(object sender, RoutedEventArgs e)
        {
            var section = (FrameworkElement)sender;
            if (!_sections.TryGetValue(section, out var state))
            {
                state = new SectionViewState(_visit);
                state.CaptureDefaults(section);
                _sections.Add(section, state);
            }
            else if (state.Visit != _visit)
            {
                state.Visit = _visit;
                state.RestoreDefaults();
            }
        }

        private sealed class SectionViewState(int visit)
        {
            internal int Visit = visit;
            private WeakReference<ScrollViewer>? _scroll;
            private readonly List<(WeakReference<SettingsExpander> Expander, bool Expanded)> _expanders = [];

            // Each lazily created section is inspected once. Reentry restores only its own view state.
            internal void CaptureDefaults(DependencyObject element)
            {
                if (_scroll is null && element is ScrollViewer scroll)
                    _scroll = new(scroll);
                if (element is SettingsExpander expander)
                    _expanders.Add((new(expander), expander.IsExpanded));

                int count = VisualTreeHelper.GetChildrenCount(element);
                for (int i = 0; i < count; i++)
                    CaptureDefaults(VisualTreeHelper.GetChild(element, i));
            }

            internal void RestoreDefaults()
            {
                foreach (var (reference, expanded) in _expanders)
                    if (reference.TryGetTarget(out var expander) && expander.IsExpanded != expanded)
                        expander.IsExpanded = expanded;

                if (_scroll?.TryGetTarget(out var scroll) == true &&
                    (scroll.HorizontalOffset != 0 || scroll.VerticalOffset != 0))
                    scroll.ChangeView(0, 0, null, disableAnimation: true);
            }
        }
    }
}