using Microsoft.UI.Xaml.Data;
using System;

namespace WinUIMusicPlayer.Converters;

public sealed class CollectionViewGroupsConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
        => (value as ICollectionView)?.CollectionGroups;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
