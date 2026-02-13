using System;
using System.Collections.ObjectModel;
using ZLinq;

namespace WinUIMusicPlayer.Extensions
{
    public static class ObservableCollectionExtensions
    {
        public static void Shuffle<T>(this ObservableCollection<T> collection)
        {
            Random rng = new();
            int n = collection.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (collection[n], collection[k]) = (collection[k], collection[n]);
            }
        }

        public static ObservableCollection<T> CreateShuffled<T>(this ObservableCollection<T> originalCollection)
        {
            if (originalCollection is null || originalCollection.Count == 0)
            {
                return [];
            }

            var list = originalCollection.AsValueEnumerable().ToList();

            Random rng = new();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                (list[n], list[k]) = (list[k], list[n]);
            }
            return new ObservableCollection<T>(list);
        }
    }
}
