using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace WinUIMusicPlayer.Helper
{
    public static class RichTextBlockHelper
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(RichTextBlockHelper),
                new PropertyMetadata(null, OnTextChanged));

        public static string GetText(DependencyObject obj)
        {
            return (string)obj.GetValue(TextProperty);
        }

        public static void SetText(DependencyObject obj, string value)
        {
            obj.SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RichTextBlock richTextBlock && e.NewValue is string text)
            {
                richTextBlock.Blocks.Clear();
                if (!string.IsNullOrEmpty(text))
                {
                    var paragraph = new Paragraph();
                    paragraph.Inlines.Add(new Run { Text = text });
                    richTextBlock.Blocks.Add(paragraph);
                }
            }
        }
    }
}
