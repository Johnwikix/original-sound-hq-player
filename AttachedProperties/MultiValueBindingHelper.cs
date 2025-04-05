using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.AttachedProperties
{
    public static class MultiValueBindingHelper
    {
        public static readonly DependencyProperty BindingsProperty =
            DependencyProperty.RegisterAttached("Bindings", typeof(MultiBindingInfo), typeof(MultiValueBindingHelper), new PropertyMetadata(null, OnBindingsChanged));

        public static MultiBindingInfo GetBindings(DependencyObject obj)
        {
            return (MultiBindingInfo)obj.GetValue(BindingsProperty);
        }

        public static void SetBindings(DependencyObject obj, MultiBindingInfo value)
        {
            obj.SetValue(BindingsProperty, value);
        }

        private static void OnBindingsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement element && e.NewValue is MultiBindingInfo info)
            {
                var binding1 = info.Binding1;
                var binding2 = info.Binding2;
                var converter = info.Converter;

                BindingOperations.SetBinding(element, BindingTargetProperty, binding1);
                BindingOperations.SetBinding(element, BindingTarget2Property, binding2);

                element.RegisterPropertyChangedCallback(BindingTargetProperty, (sender, prop) =>
                {
                    UpdateValue(sender, converter);
                });

                element.RegisterPropertyChangedCallback(BindingTarget2Property, (sender, prop) =>
                {
                    UpdateValue(sender, converter);
                });
            }
        }

        private static readonly DependencyProperty BindingTargetProperty =
            DependencyProperty.RegisterAttached("BindingTarget", typeof(object), typeof(MultiValueBindingHelper), new PropertyMetadata(null));

        private static readonly DependencyProperty BindingTarget2Property =
            DependencyProperty.RegisterAttached("BindingTarget2", typeof(object), typeof(MultiValueBindingHelper), new PropertyMetadata(null));

        private static void UpdateValue(DependencyObject d, IValueConverter converter)
        {
            if (d is FrameworkElement element)
            {
                var value1 = element.GetValue(BindingTargetProperty);
                var value2 = element.GetValue(BindingTarget2Property);

                if (value1 is int sampleRate && value2 is int bitDepth)
                {
                    var result = converter.Convert(Tuple.Create(sampleRate, bitDepth), typeof(Visibility), null, null);
                    if (element is Image image)
                    {
                        image.Visibility = (Visibility)result;
                    }
                }
            }
        }
    }

    public class MultiBindingInfo
    {
        public Binding Binding1 { get; set; }
        public Binding Binding2 { get; set; }
        public IValueConverter Converter { get; set; }
    }
}
