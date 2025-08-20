using System;
using Windows.Storage;

namespace MangaViewer.Services
{
    /// <summary>
    /// Stores user customizable tag related settings (e.g., font size) in local settings.
    /// </summary>
    public sealed class TagSettingsService
    {
        public static TagSettingsService Instance { get; } = new();
        private readonly ApplicationDataContainer _local = ApplicationData.Current.LocalSettings;
        private const string FontSizeKey = "TagFontSize";
        private double _tagFontSize = 13d;

        private TagSettingsService()
        {
            try
            {
                if (_local.Values.TryGetValue(FontSizeKey, out var v))
                {
                    if (v is double d) _tagFontSize = d;
                    else if (v is int i) _tagFontSize = i;
                    else if (double.TryParse(v?.ToString(), out var p)) _tagFontSize = p;
                }
            }
            catch { _tagFontSize = 13d; }
        }

        public event EventHandler? TagFontSizeChanged;

        public double TagFontSize
        {
            get => _tagFontSize;
            set
            {
                double clamped = Math.Clamp(value, 6d, 48d);
                if (Math.Abs(clamped - _tagFontSize) < 0.1) return;
                _tagFontSize = clamped;
                try { _local.Values[FontSizeKey] = clamped; } catch { }
                TagFontSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
