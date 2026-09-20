using CommunityToolkit.Mvvm.ComponentModel;
using System;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

namespace WinUIMusicPlayer.State;

/// <summary>Audio 领域偏好的可观察状态；不执行 I/O 或启动后台任务。</summary>
public sealed class AudioPreferencesState : ObservableObject
{
    public int Latency { get; set => SetProperty(ref field, value); } = 300;
    public int DsdGain { get; set => SetProperty(ref field, value); } = 6;
    public bool IsFadeEnabled { get; set => SetProperty(ref field, value); }
    public int DsdPcmFreq { get; set => SetProperty(ref field, value); } = 88200;
}
