using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Pages
{
    public partial class MainViewModel:ObservableObject
    {
        public AppViewModel AppViewModel { get;}
        public BassPlayerCommandService PlayerCommandService { get;}
        public MainViewModel(AppViewModel appViewModel, BassPlayerCommandService playerCommandService)
        {
            AppViewModel = appViewModel;
            PlayerCommandService = playerCommandService;
        }
    }
}
