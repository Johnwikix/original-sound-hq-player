using ObservableCollections;
using System;

namespace WinUIMusicPlayer.Helper
{
    public sealed class LambdaFilter<T, TView> : ISynchronizedViewFilter<T, TView>
    {
        private readonly Func<T, bool> _predicate;
        public LambdaFilter(Func<T, bool> predicate) { _predicate = predicate; }
        public bool IsMatch(T value, TView view) => _predicate(value);
    }
}




