using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace WinUIMusicPlayer.Extensions
{
    public static class ObservableCollectionExtensions
    {
        public static void Shuffle<T>(this ObservableCollection<T> collection)
        {
            if (collection is null || collection.Count == 0) return;
            var rng = Random.Shared;
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

            // ObservableCollection<T> 实现 IList<T>,走 ctor (IList<T>) 最优路径(避免 enumerator)
            var list = new List<T>(originalCollection);

            var rng = Random.Shared;
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
