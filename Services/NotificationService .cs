using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace WinUIMusicPlayer.Services
{
    public class NotificationService
    {
        public void SendNotification(string title, string content)
        {
            // 创建 Toast 内容
            var toastContent = new ToastContent()
            {
                Visual = new ToastVisual()
                {
                    BindingGeneric = new ToastBindingGeneric()
                    {
                        Children =
                        {
                            new AdaptiveText()
                            {
                                Text = title
                            },
                            new AdaptiveText()
                            {
                                Text = content
                            }
                        }
                    }
                },
                Actions = new ToastActionsCustom()
                {
                    Buttons =
                    {
                        new ToastButton("关闭", "action=close")
                           .SetBackgroundActivation()
                    }
                }
            };
            var toast = new ToastNotification(toastContent.GetXml());
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }
    }
}
