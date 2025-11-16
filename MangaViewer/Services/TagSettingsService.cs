using System;
using MangaViewer.Services;

namespace MangaViewer.Services
{
    /// <summary>
    /// Stores user customizable tag related settings (e.g., font size).
    /// WinRT ApplicationData dependency removed in favor of SettingsProvider for broader host support.
    /// </summary>
    public sealed class TagSettingsService
    {
        public static TagSettingsService Instance { get; } = new();
        private const string FontSizeKey = "TagFontSize";
        private double _tagFontSize = 13d;

        private TagSettingsService()
        {
            try
            {
                _tagFontSize = SettingsProvider.GetDouble(FontSizeKey, 13d);
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
                try { SettingsProvider.SetDouble(FontSizeKey, clamped); } catch { }
                TagFontSizeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
