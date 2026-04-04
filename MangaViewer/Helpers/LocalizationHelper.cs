using Windows.ApplicationModel.Resources;

namespace MangaViewer.Helpers
{
    public static class LocalizationHelper
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForViewIndependentUse();

        public static string GetString(string key, string fallback)
        {
            try
            {
                var value = Loader.GetString(key);
                return string.IsNullOrWhiteSpace(value) ? fallback : value;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
