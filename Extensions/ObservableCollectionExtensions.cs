using System;
using System.Collections.ObjectModel;
using ZLinq;

namespace WinUIMusicPlayer.Extensions
{
    public static class ObservableCollectionExtensions
    {
        public static void Shuffle<T>(this ObservableCollection<T> collection)
        {
            Random rng = new Random();
            int n = collection.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = collection[k];
                collection[k] = collection[n];
                collection[n] = value;
            }
        }

        public static ObservableCollection<T> CreateShuffled<T>(this ObservableCollection<T> originalCollection)
        {
            if (originalCollection is null || originalCollection.Count == 0)
            {
                return [];
            }

            var list = originalCollection.AsValueEnumerable().ToList();

            Random rng = new Random();
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                T value = list[k];
                list[k] = list[n];
                list[n] = value;
            }

            return new ObservableCollection<T>(list);
        }
    }
}
