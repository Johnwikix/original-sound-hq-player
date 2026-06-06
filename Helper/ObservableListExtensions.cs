using ObservableCollections;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Helper
{
    public static class ObservableListExtensions
    {
        public static void ShuffleInPlace<T>(this ObservableList<T> list, Random rng)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                if (k != n) list.Move(n, k);
            }
        }

        public static void RestoreOrder<T>(this ObservableList<T> list, IReadOnlyList<T> snapshot) where T : notnull
        {
            if (list.Count != snapshot.Count)
            {
                throw new ArgumentException("snapshot size must match list size", nameof(snapshot));
            }

            var currentPositions = new Dictionary<T, int>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                currentPositions[list[i]] = i;
            }

            for (int targetIndex = 0; targetIndex < snapshot.Count; targetIndex++)
            {
                T item = snapshot[targetIndex];
                int currentIndex = currentPositions[item];
                if (currentIndex != targetIndex)
                {
                    list.Move(currentIndex, targetIndex);
                    var displaced = list[targetIndex];
                    currentPositions[displaced] = currentIndex;
                    currentPositions[item] = targetIndex;
                }
            }
        }
    }

    public sealed class LambdaFilter<T, TView> : ISynchronizedViewFilter<T, TView>
    {
        private readonly Func<T, bool> _predicate;
        public LambdaFilter(Func<T, bool> predicate) { _predicate = predicate; }
        public bool IsMatch(T value, TView view) => _predicate(value);
    }
}


