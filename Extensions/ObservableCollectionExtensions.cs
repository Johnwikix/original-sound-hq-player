using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
            if (originalCollection == null || originalCollection.Count == 0)
            {
                return [];
            }
            // 先将原始集合复制到列表中，这是为了执行高效的洗牌操作。
            var list = originalCollection.ToList();

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

            // 使用洗牌后的列表创建一个新的 ObservableCollection
            return new ObservableCollection<T>(list);
        }
    }
}
