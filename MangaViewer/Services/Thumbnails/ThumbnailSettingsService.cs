using System;
using Windows.Storage;

namespace MangaViewer.Services.Thumbnails
{
    /// <summary>
    /// 썸네일 관련 설정 저장/알림 서비스. Decode 폭 등.
    /// </summary>
    public sealed class ThumbnailSettingsService
    {
        public static ThumbnailSettingsService Instance { get; } = new();
        private readonly ApplicationDataContainer _local = ApplicationData.Current.LocalSettings;
        private const string DecodeWidthKey = "ThumbDecodeWidth";

        private int _decodeWidth = 150;

        private ThumbnailSettingsService()
        {
            try
            {
                if (_local.Values.TryGetValue(DecodeWidthKey, out var w)
                    && int.TryParse(w?.ToString(), out var parsed) && parsed > 0)
                {
                    _decodeWidth = parsed;
                }
            }
            catch { _decodeWidth = 150; }
        }

        public event EventHandler? SettingsChanged;

        public int DecodeWidth
        {
            get => _decodeWidth;
            set
            {
                int clamped = Math.Clamp(value, 64, 1024);
                if (clamped == _decodeWidth) return;
                _decodeWidth = clamped;
                try { _local.Values[DecodeWidthKey] = clamped; } catch { }
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
