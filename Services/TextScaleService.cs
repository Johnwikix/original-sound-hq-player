using Microsoft.UI.Dispatching;
using System.ComponentModel;
using Windows.UI.ViewManagement;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 系统文本缩放因子（设置 &gt; 辅助功能 &gt; 文本大小）的绑定源。
    /// ActualWidth/ActualHeight 不发属性变更通知，无法直接驱动 x:Bind 联动布局，
    /// 故由本服务统一监听 UISettings.TextScaleFactorChanged，以 INPC 暴露当前因子；
    /// 页面通过 x:Bind 绑定 TextScaleFactor 即可随系统设置实时缩放。
    /// </summary>
    public sealed class TextScaleService : INotifyPropertyChanged
    {
        // 首次访问须发生在 UI 线程（构造时捕获 DispatcherQueue）；首次 x:Bind 求值即在 UI 线程完成。
        public static TextScaleService Instance { get; } = new TextScaleService();

        private readonly UISettings _uiSettings = new UISettings();
        private readonly DispatcherQueue _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        public event PropertyChangedEventHandler PropertyChanged;

        public double TextScaleFactor => _uiSettings.TextScaleFactor;

        private TextScaleService()
        {
            _uiSettings.TextScaleFactorChanged += OnTextScaleFactorChanged;
        }

        private void OnTextScaleFactorChanged(UISettings sender, object args)
        {
            // UISettings 事件在后台线程触发，须回 UI 线程再通知绑定
            _dispatcherQueue.TryEnqueue(() =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextScaleFactor))));
        }
    }
}
