using System.Windows;

namespace FancyWM.ThemeEngine.Wpf
{
    public static class CssManager
    {
        private static volatile IReadOnlyDictionary<string, CssValue> _current = new Dictionary<string, CssValue>();

        public static event Action? ThemeChanged;

        public static IReadOnlyDictionary<string, CssValue> Current => _current;

        public static void ApplyTheme(IReadOnlyDictionary<string, CssValue> frozenDict)
        {
            ArgumentNullException.ThrowIfNull(frozenDict);

            Interlocked.Exchange(ref _current, frozenDict);

            if (Application.Current.Dispatcher.CheckAccess())
            {
                ThemeChanged?.Invoke();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() => ThemeChanged?.Invoke());
            }
        }

        public static CssValue? Resolve(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (_current.Count == 0)
            {
                return null;
            }
            if (_current.TryGetValue(path, out var val))
            {
                return val;
            }
            else
            {
                return null;
            }
        }
    }
}
