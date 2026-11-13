using System.Threading.Tasks;

namespace FancyWM.Utilities
{
    internal static class Autostart
    {
        public static Task<bool> IsEnabledAsync() => Task.FromResult(false);

        public static Task<bool> EnableAsync() => Task.FromResult(false);

        public static Task<bool> DisableAsync() => Task.FromResult(true);
    }
}
